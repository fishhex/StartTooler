// StartTooler 公网代理（Go 实现）
//
// VPS 上跑，监听两个端口：
//   - HTTP_PORT: 返回 index.html + 接收 multipart 上传 + /ack/{id}
//   - TCP_PORT: 接受本地 StartTooler 长连接，单向推送 file_pending 通知
//
// 文件流转：
//   1. HTTP 接收 → tmp/<id>.bin + tmp/<id>.meta → 写入 state.pending
//   2. State.Broadcast(p) 通知所有已连 TCP 客户端（C# 端收 file_pending）
//   3. C# 端用 SSH scp 拉文件到本地项目目录（按日期归档）
//   4. C# 端拉完调 HTTP POST /ack/{id} → Go relay 从 pending 删 + rm tmp 文件
//
// 依赖：纯 Go 标准库，零第三方包。HTML 用 go:embed 内嵌。
//
// Usage:
//   upload-relay --http-port 8765 --tcp-port 8766 --tmp-dir /tmp/uploads
//
// SIGTERM 优雅退出。
package main

import (
	_ "embed"
	"encoding/hex"
	"encoding/json"
	"flag"
	"fmt"
	"io"
	"log"
	"math/rand"
	"net"
	"net/http"
	"os"
	"os/signal"
	"path/filepath"
	"strings"
	"sync"
	"syscall"
	"time"
)

//go:embed web/index.html
var indexHTML string

const version = "1.0.0"

// ============================================================
// 共享状态
// ============================================================

type pendingFile struct {
	ID   string
	Name string
	Path string
}

type State struct {
	mu          sync.Mutex
	tmpDir      string
	pending     map[string]pendingFile
	startedAt   time.Time
	subMu       sync.Mutex
	subscribers []chan pendingFile
}

func newState(tmpDir string) *State {
	return &State{
		tmpDir:    tmpDir,
		pending:   make(map[string]pendingFile),
		startedAt: time.Now(),
	}
}

func newID() string {
	// 32 字符 hex，跟 Python 版 `uuid.uuid4().hex` 完全兼容
	b := make([]byte, 16)
	// math/rand 用于生成 id（不要求密码学强度，但要 unique）
	// 注意：Go 1.20+ math/rand 自动 seed，1.19- 需要手动 seed
	b[0] = byte(rand.Intn(256))
	if _, err := rand.Read(b[1:]); err != nil {
		log.Fatalf("rand.Read: %v", err)
	}
	return hex.EncodeToString(b)
}

func (s *State) saveUploaded(filename string, data []byte) (string, error) {
	id := newID()
	binPath := filepath.Join(s.tmpDir, id+".bin")
	metaPath := filepath.Join(s.tmpDir, id+".meta")

	if err := os.WriteFile(binPath, data, 0644); err != nil {
		return "", fmt.Errorf("write bin: %w", err)
	}
	meta, _ := json.Marshal(map[string]string{"id": id, "name": filename})
	if err := os.WriteFile(metaPath, meta, 0644); err != nil {
		os.Remove(binPath)
		return "", fmt.Errorf("write meta: %w", err)
	}

	s.mu.Lock()
	s.pending[id] = pendingFile{ID: id, Name: filename, Path: binPath}
	s.mu.Unlock()

	log.Printf("saved %s -> %s (%d bytes)", filename, id, len(data))

	// 通知所有已连 TCP 客户端
	s.Broadcast(pendingFile{ID: id, Name: filename, Path: binPath})
	return id, nil
}

func (s *State) listPending() []pendingFile {
	s.mu.Lock()
	defer s.mu.Unlock()
	out := make([]pendingFile, 0, len(s.pending))
	for _, p := range s.pending {
		if _, err := os.Stat(p.Path); err == nil {
			out = append(out, p)
		}
	}
	return out
}

func (s *State) getPending(id string) (pendingFile, bool) {
	s.mu.Lock()
	defer s.mu.Unlock()
	p, ok := s.pending[id]
	return p, ok
}

// Ack: C# 端拉完文件后调用，从 pending 删 + 删 tmp 文件
func (s *State) Ack(id string) error {
	s.mu.Lock()
	p, ok := s.pending[id]
	if ok {
		delete(s.pending, id)
	}
	s.mu.Unlock()
	if !ok {
		return fmt.Errorf("unknown id: %s", id)
	}
	binErr := os.Remove(p.Path)
	metaErr := os.Remove(p.Path[:len(p.Path)-4] + ".meta")
	if binErr != nil && !os.IsNotExist(binErr) {
		return binErr
	}
	if metaErr != nil && !os.IsNotExist(metaErr) {
		return metaErr
	}
	log.Printf("acked & deleted %s (%s)", p.Name, id)
	return nil
}

// Subscribe: TCP 客户端连上时订阅通知
func (s *State) Subscribe() chan pendingFile {
	ch := make(chan pendingFile, 16)
	s.subMu.Lock()
	s.subscribers = append(s.subscribers, ch)
	s.subMu.Unlock()
	return ch
}

// Unsubscribe: TCP 客户端断开时取消订阅
func (s *State) Unsubscribe(ch chan pendingFile) {
	s.subMu.Lock()
	defer s.subMu.Unlock()
	for i, c := range s.subscribers {
		if c == ch {
			s.subscribers = append(s.subscribers[:i], s.subscribers[i+1:]...)
			close(ch)
			return
		}
	}
}

// ReplayPending: 新 client subscribe 后补发当前所有 pending。
// 解决：client 断线期间上传的文件，重连后会重新收到通知。
// 满了丢（client 来不及消费就丢，反正 pending map 还在，client 端 ack 失败可以重试）。
func (s *State) ReplayPending(ch chan pendingFile) {
	s.mu.Lock()
	defer s.mu.Unlock()
	for _, p := range s.pending {
		select {
		case ch <- p:
		default:
		}
	}
}

// Broadcast: handleUpload 收完文件后调用，fan-out 给所有已订阅客户端
// 满了丢（client 来不及处理就丢，不会阻塞）
func (s *State) Broadcast(p pendingFile) {
	s.subMu.Lock()
	defer s.subMu.Unlock()
	for _, ch := range s.subscribers {
		select {
		case ch <- p:
		default:
			// 客户端 buffer 满，跳过这一条；下次 ping 触发重传（但本协议没 ping 机制）
			// 实际影响极小：单 client + 16 buffer，正常场景下不会满
		}
	}
}

// SubscriberCount: /health 用
func (s *State) SubscriberCount() int {
	s.subMu.Lock()
	defer s.subMu.Unlock()
	return len(s.subscribers)
}

// ============================================================
// HTTP server
// ============================================================

func runHTTP(port int, html string) {
	mux := http.NewServeMux()
	state := getHTTPState()

	mux.HandleFunc("/upload", func(w http.ResponseWriter, r *http.Request) {
		// 关键：GET /upload 要返回 HTML 上传页（QR 扫码进来是 GET）
		// POST /upload 才是接收 multipart 文件
		// Go ServeMux 1.22 之前 HandleFunc("/upload",...) 是精确匹配，
		// 不会 fall through 到下面 "/" 兜底 handler，所以 method 分流必须在这里完成。
		switch r.Method {
		case http.MethodGet:
			serveIndexHTML(w, r, html)
		case http.MethodPost:
			handleUpload(w, r, state)
		default:
			http.Error(w, "Method not allowed", http.StatusMethodNotAllowed)
		}
	})
	mux.HandleFunc("/health", func(w http.ResponseWriter, r *http.Request) {
		// 健康检查：返回进程状态 + 运行时指标
		// 用途：监控 / 探活 / StartTooler 远程确认服务在线
		if r.Method != http.MethodGet {
			http.Error(w, "GET required", http.StatusMethodNotAllowed)
			return
		}
		pending := state.listPending()
		w.Header().Set("Content-Type", "application/json")
		w.Header().Set("Cache-Control", "no-store")
		_ = json.NewEncoder(w).Encode(map[string]interface{}{
			"status":         "ok",
			"version":        version,
			"uptime_seconds": int64(time.Since(state.startedAt).Seconds()),
			"pending_files":  len(pending),
			"tcp_clients":    state.SubscriberCount(),
		})
	})
	mux.HandleFunc("/ack/", func(w http.ResponseWriter, r *http.Request) {
		// C# 端 SSH scp 拉完文件后回调，删 VPS 端 tmp 文件
		// URL: POST /ack/{id}
		if r.Method != http.MethodPost {
			http.Error(w, "POST required", http.StatusMethodNotAllowed)
			return
		}
		// r.URL.Path = "/ack/{id}"，取最后一段
		id := strings.TrimPrefix(r.URL.Path, "/ack/")
		if id == "" || strings.Contains(id, "/") {
			http.Error(w, "missing or invalid id", http.StatusBadRequest)
			return
		}
		if err := state.Ack(id); err != nil {
			http.Error(w, err.Error(), http.StatusNotFound)
			return
		}
		w.Header().Set("Content-Type", "application/json")
		_, _ = io.WriteString(w, `{"ok":true}`+"\n")
	})
	mux.HandleFunc("/", func(w http.ResponseWriter, r *http.Request) {
		// 兜底：/ 或任何非 /upload / /health / /ack 的 GET 路径都返回 HTML
		// /upload 会被上面的 handler 精确匹配抢走，POST /upload 才进 handleUpload
		if r.Method != http.MethodGet {
			http.Error(w, "Method not allowed", http.StatusMethodNotAllowed)
			return
		}
		serveIndexHTML(w, r, html)
	})

	addr := fmt.Sprintf(":%d", port)
	log.Printf("HTTP server listening on %s", addr)
	if err := http.ListenAndServe(addr, mux); err != nil {
		log.Printf("HTTP server error: %v", err)
	}
}

// 全局 state 由 main 通过 setter 注入（保持 http handler 简单）
var httpState *State

func getHTTPState() *State { return httpState }

func serveIndexHTML(w http.ResponseWriter, r *http.Request, html string) {
	w.Header().Set("Content-Type", "text/html; charset=utf-8")
	w.Header().Set("Cache-Control", "no-store")
	// 模板替换：{{STARTOOLER_BASE}} -> http://host
	host := r.Host
	if host == "" {
		host = "localhost"
	}
	body := strings.ReplaceAll(html, "{{STARTOOLER_BASE}}", "http://"+host)
	_, _ = io.WriteString(w, body)
}

func handleUpload(w http.ResponseWriter, r *http.Request, state *State) {
	if r.Method != http.MethodPost {
		http.Error(w, "POST required", http.StatusMethodNotAllowed)
		return
	}
	// 32MB threshold，超出 spill 到临时文件；之后整体读到内存（跟 Python 版语义一致）
	if err := r.ParseMultipartForm(32 << 20); err != nil {
		http.Error(w, err.Error(), http.StatusBadRequest)
		return
	}
	if r.MultipartForm == nil {
		http.Error(w, "no multipart", http.StatusBadRequest)
		return
	}

	// 收集所有名为 "file" 的 part（HTML 前端固定用 "file"）
	files := r.MultipartForm.File["file"]
	// 兼容：也接受单文件
	if len(files) == 0 {
		for _, v := range r.MultipartForm.File {
			files = append(files, v...)
		}
	}

	success := 0
	for _, fh := range files {
		f, err := fh.Open()
		if err != nil {
			log.Printf("open part: %v", err)
			continue
		}
		data, err := io.ReadAll(f)
		f.Close()
		if err != nil {
			log.Printf("read part: %v", err)
			continue
		}
		name := filepath.Base(fh.Filename)
		if name == "" || name == "." || name == "/" {
			name = fmt.Sprintf("upload_%s.bin", time.Now().Format("150405"))
		}
		if _, err := state.saveUploaded(name, data); err != nil {
			log.Printf("save: %v", err)
			continue
		}
		success++
	}

	w.Header().Set("Content-Type", "application/json")
	_ = json.NewEncoder(w).Encode(map[string]interface{}{
		"success": true,
		"count":   success,
	})
}

// ============================================================
// TCP server（单向推送 file_pending 通知）
// ============================================================

func runTCP(port int, state *State) {
	ln, err := net.Listen("tcp", fmt.Sprintf(":%d", port))
	if err != nil {
		log.Fatalf("TCP listen: %v", err)
	}
	defer ln.Close()
	log.Printf("TCP server listening on :%d (file_pending notify)", port)

	for {
		conn, err := ln.Accept()
		if err != nil {
			log.Printf("TCP accept: %v", err)
			continue
		}
		// TCP keepalive：防 NAT 老化
		if tcpConn, ok := conn.(*net.TCPConn); ok {
			_ = tcpConn.SetKeepAlive(true)
			_ = tcpConn.SetKeepAlivePeriod(30 * time.Second)
		}
		go handleTCPClient(conn, state)
	}
}

func handleTCPClient(conn net.Conn, state *State) {
	defer conn.Close()
	remote := conn.RemoteAddr().String()
	log.Printf("TCP client connected: %s", remote)

	ch := state.Subscribe()
	defer state.Unsubscribe(ch)

	// 新连接补发当前所有 pending（断线期间上传的文件也能收到通知）
	state.ReplayPending(ch)

	// 单独 goroutine 跑 read：仅用于消费 client 消息（暂时忽略），保持 TCP 活跃
	readErr := make(chan error, 1)
	go func() {
		buf := make([]byte, 1024)
		for {
			conn.SetReadDeadline(time.Now().Add(180 * time.Second))
			_, err := conn.Read(buf)
			if err != nil {
				readErr <- err
				return
			}
		}
	}()

	for {
		select {
		case err := <-readErr:
			if ne, ok := err.(net.Error); ok && ne.Timeout() {
				log.Printf("TCP client %s idle timeout", remote)
			} else {
				log.Printf("TCP client %s read err: %v", remote, err)
			}
			return
		case p, ok := <-ch:
			if !ok {
				log.Printf("TCP client %s: state shutting down", remote)
				return
			}
			stat, statErr := os.Stat(p.Path)
			size := int64(0)
			if statErr == nil {
				size = stat.Size()
			}
			// 通知 JSON（不传文件体，C# 端走 SSH scp 拉取）
			msg := fmt.Sprintf(
				`{"type":"file_pending","id":"%s","name":"%s","size":%d}`+"\n",
				p.ID, p.Name, size)
			conn.SetWriteDeadline(time.Now().Add(5 * time.Second))
			if _, err := conn.Write([]byte(msg)); err != nil {
				log.Printf("TCP client %s notify write: %v", remote, err)
				return
			}
			log.Printf("notified %s: %s id=%s size=%d", remote, p.Name, p.ID, size)
		}
	}
}

// ============================================================
// 入口
// ============================================================

func main() {
	httpPort := flag.Int("http-port", 8765, "HTTP server port")
	tcpPort := flag.Int("tcp-port", 8766, "TCP server port (file_pending notify)")
	tmpDir := flag.String("tmp-dir", "", "temp file directory (required)")
	showVer := flag.Bool("version", false, "print version and exit")
	flag.Parse()

	if *showVer {
		fmt.Printf("upload-relay v%s\n", version)
		return
	}

	if *tmpDir == "" {
		log.Fatal("--tmp-dir is required")
	}
	if err := os.MkdirAll(*tmpDir, 0755); err != nil {
		log.Fatalf("mkdir tmp: %v", err)
	}

	state := newState(*tmpDir)
	httpState = state

	// HTTP server 独立 goroutine（ListenAndServe 不返回）
	go runHTTP(*httpPort, indexHTML)

	// TCP server 在 main loop
	tcpDone := make(chan struct{})
	go func() {
		defer close(tcpDone)
		runTCP(*tcpPort, state)
	}()

	// 优雅退出
	sig := make(chan os.Signal, 1)
	signal.Notify(sig, syscall.SIGTERM, syscall.SIGINT)
	sigRecv := <-sig
	log.Printf("received signal %v, shutting down", sigRecv)
}

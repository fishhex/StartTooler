// StartTooler 公网代理（Go 实现）
//
// VPS 上跑，监听两个端口：
//   - HTTP_PORT: 返回 index.html + 接收 multipart 上传
//   - TCP_PORT: 接受本地 StartTooler 长连接，推送已上传文件
//
// 文件流转：HTTP 接收 → tmp/<id>.bin + tmp/<id>.meta → 通过 TCP 推给所有已连接客户端 → 等 ack → 删文件
//
// 依赖：纯 Go 标准库，零第三方包。编译为单文件二进制，HTML 用 go:embed 内嵌。
//
// Usage:
//   upload-relay --http-port 8765 --tcp-port 8766 --tmp-dir /tmp/uploads
//
// SIGTERM 优雅退出。
package main

import (
	"bufio"
	"crypto/rand"
	_ "embed"
	"encoding/binary"
	"encoding/hex"
	"encoding/json"
	"flag"
	"fmt"
	"io"
	"log"
	"net"
	"net/http"
	"os"
	"os/signal"
	"path/filepath"
	"strings"
	"sync"
	"sync/atomic"
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
	mu         sync.Mutex
	tmpDir     string
	pending    map[string]pendingFile
	startedAt  time.Time
	tcpClients int32 // atomic, 当前已连接 TCP 客户端数
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
	if _, err := rand.Read(b); err != nil {
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

func (s *State) delete(id string) {
	s.mu.Lock()
	delete(s.pending, id)
	s.mu.Unlock()
	os.Remove(filepath.Join(s.tmpDir, id+".bin"))
	os.Remove(filepath.Join(s.tmpDir, id+".meta"))
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
			"tcp_clients":    atomic.LoadInt32(&state.tcpClients),
		})
	})
	mux.HandleFunc("/", func(w http.ResponseWriter, r *http.Request) {
		// 兜底：/ 或任何非 /upload / /health 的 GET 路径都返回 HTML
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
// TCP server
// ============================================================

func runTCP(port int, state *State) {
	ln, err := net.Listen("tcp", fmt.Sprintf(":%d", port))
	if err != nil {
		log.Fatalf("TCP listen: %v", err)
	}
	defer ln.Close()
	log.Printf("TCP server listening on :%d", port)

	for {
		conn, err := ln.Accept()
		if err != nil {
			log.Printf("TCP accept: %v", err)
			continue
		}
		go handleTCPClient(conn, state)
	}
}

func handleTCPClient(conn net.Conn, state *State) {
	atomic.AddInt32(&state.tcpClients, 1)
	defer func() {
		atomic.AddInt32(&state.tcpClients, -1)
		conn.Close()
	}()
	log.Printf("TCP client connected: %s", conn.RemoteAddr())
	reader := bufio.NewReader(conn)

	// 主循环：等客户端心跳 / 新指令，推所有 pending
	for {
		// 等客户端消息（ping）或新指令，带超时用于心跳触发
		conn.SetReadDeadline(time.Now().Add(120 * time.Second))
		line, err := reader.ReadBytes('\n')
		if err != nil {
			if ne, ok := err.(net.Error); ok && ne.Timeout() {
				// 心跳超时，扫一次 pending 即可
				if !pushAllPending(conn, reader, state) {
					return
				}
				continue
			}
			log.Printf("TCP read: %v", err)
			return
		}

		var msg map[string]interface{}
		if err := json.Unmarshal(bytesTrimSpace(line), &msg); err != nil {
			log.Printf("TCP parse: %v", err)
			continue
		}

		switch msg["type"] {
		case "ping":
			conn.SetWriteDeadline(time.Now().Add(10 * time.Second))
			_, _ = conn.Write([]byte(`{"type":"pong"}` + "\n"))
		}
		// 任何消息都触发一次 pending 扫描推送
		if !pushAllPending(conn, reader, state) {
			return
		}
	}
}

func bytesTrimSpace(b []byte) []byte {
	start, end := 0, len(b)
	for start < end && (b[start] == ' ' || b[start] == '\t' || b[start] == '\r') {
		start++
	}
	for end > start && (b[end-1] == ' ' || b[end-1] == '\t' || b[end-1] == '\r' || b[end-1] == '\n') {
		end--
	}
	return b[start:end]
}

func pushAllPending(conn net.Conn, reader *bufio.Reader, state *State) bool {
	pending := state.listPending()
	for _, p := range pending {
		if !pushOne(conn, reader, state, p) {
			return false
		}
	}
	return true
}

func pushOne(conn net.Conn, reader *bufio.Reader, state *State, p pendingFile) bool {
	file, err := os.Open(p.Path)
	if err != nil {
		log.Printf("open %s: %v", p.Path, err)
		return true // 单个文件出错不影响后续
	}
	defer file.Close()

	stat, err := file.Stat()
	if err != nil {
		return true
	}
	size := stat.Size()

	header, _ := json.Marshal(map[string]interface{}{
		"type": "file",
		"id":   p.ID,
		"name": p.Name,
		"size": size,
	})
	// C# 客户端期望：4 字节大端长度 + JSON + '\n' + 二进制体
	var lenBuf [4]byte
	binary.BigEndian.PutUint32(lenBuf[:], uint32(len(header)))

	conn.SetWriteDeadline(time.Now().Add(60 * time.Second))
	if _, err := conn.Write(lenBuf[:]); err != nil {
		log.Printf("write len: %v", err)
		return false
	}
	if _, err := conn.Write(header); err != nil {
		return false
	}
	if _, err := conn.Write([]byte{'\n'}); err != nil {
		return false
	}
	if _, err := io.Copy(conn, file); err != nil {
		return false
	}

	// 等 ack（带超时）
	conn.SetReadDeadline(time.Now().Add(60 * time.Second))
	ackLine, err := reader.ReadBytes('\n')
	if err != nil {
		log.Printf("ack read: %v", err)
		return false
	}
	var ack map[string]interface{}
	if err := json.Unmarshal(bytesTrimSpace(ackLine), &ack); err != nil {
		log.Printf("ack parse: %v", err)
		return true
	}
	if t, _ := ack["type"].(string); t == "ack" && ack["id"] == p.ID {
		state.delete(p.ID)
		log.Printf("acked & deleted %s (%s)", p.Name, p.ID)
		return true
	}
	log.Printf("ack mismatch: %v for %s", ack, p.ID)
	return true
}

// ============================================================
// 入口
// ============================================================

func main() {
	httpPort := flag.Int("http-port", 8765, "HTTP server port")
	tcpPort := flag.Int("tcp-port", 8766, "TCP server port")
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

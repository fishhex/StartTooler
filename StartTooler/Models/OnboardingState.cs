using System;

namespace StartTooler.Models;

/// <summary>
/// v0.11 spec/07 §4.3: 首次使用引导完成状态持久化 DTO。
/// 存在 <see cref="Services.ConfigKeys.Onboarding"/> 键里（config.db）。
/// </summary>
public class OnboardingState
{
    public bool Completed { get; set; }
    public DateTime? CompletedAt { get; set; }
}

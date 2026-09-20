namespace AstralPath.Shared.ViewModels;

/// <summary>
/// 页面上下文（方案 §11.8）：Shell 在导航或切换学生/教师视图时注入给页面。
/// 页面**不直接持有** Shell，避免循环依赖，也便于 Headless 单测直接构造。
/// </summary>
public sealed record PageContext(
    string StudentId,
    string StudentName,
    string TeacherId,
    string Role,
    bool TeacherSide)
{
    /// <summary>学生端视角（teacherSide=false，教师看不到该学生的热点）。</summary>
    public static PageContext ForStudent(string studentId, string studentName)
        => new(studentId, studentName, Shared.DemoMeta.TeacherId, "student", false);

    /// <summary>教师端视角（用于 §15 fail-closed 与 §46 画像的 teacherSide 参数）。</summary>
    public static PageContext ForTeacher(string studentId, string studentName)
        => new(studentId, studentName, Shared.DemoMeta.TeacherId, "teacher", true);
}

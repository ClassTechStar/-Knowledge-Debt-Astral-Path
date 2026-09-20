using AstralPath.Shared;

// 移动端共享逻辑演示：今日任务 35 分钟胶囊 + 信心滑条必填校验
Console.WriteLine("=== 知债：星穹学途 · Mobile 共享逻辑 ===");
Console.WriteLine(DemoMeta.FooterDisclaimer);
Console.WriteLine($"学生 A={DemoMeta.StudentAId} · B={DemoMeta.StudentBId} · Teacher={DemoMeta.TeacherId}");
Console.WriteLine($"图版本={DemoMeta.GraphVersion}");
Console.WriteLine();

var tasks = new[]
{
    (Kp: "K03", Name: "借贷记账法", Type: "concept", Min: 8, Why: "为还 会计等式 → 借贷记账法 的债"),
    (Kp: "K03", Name: "借贷记账法", Type: "drill", Min: 6, Why: "为还 会计等式 → 借贷记账法 的债"),
    (Kp: "K05", Name: "会计分录", Type: "quiz", Min: 6, Why: "为还 借贷记账法 → 会计分录 的债"),
};

var total = 0;
foreach (var t in tasks)
{
    total += t.Min;
    Console.WriteLine($"[{t.Type}] {t.Name} · {t.Min}min · {t.Why}");
}

Console.WriteLine();
Console.WriteLine($"今日合计 {total} 分钟（预算 35）· {"K1 绿灯"}");
Console.WriteLine("信心滑条 1–5 必填：演示默认 conf=4");
Console.WriteLine("band 标签：red=" + DemoMeta.BandLabel("red") + " yellow=" + DemoMeta.BandLabel("yellow") + " green=" + DemoMeta.BandLabel("green"));
Console.WriteLine("Mobile 逻辑演示完成。");
return total <= 35 ? 0 : 1;

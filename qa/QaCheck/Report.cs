namespace QaCheck;

/// <summary>测试报告：统一 [OK]/[FAIL]/[风险] 输出，并落盘为 UTF-8。</summary>
internal sealed class Report
{
    private readonly List<string> _lines = new();
    public int Ok { get; private set; }
    public int Fail { get; private set; }

    public void Section(string s) => Add($"\n=== {s} ===");
    public void Info(string s) => Add("       " + s);
    public void Pass(string s) { Ok++; Add("  [OK]   " + s); }
    public void Fail_(string s) { Fail++; Add("  [FAIL] " + s); }
    public void Warn(string s) => Add("  [风险] " + s);

    public void Check(bool cond, string pass, string fail)
    {
        if (cond) Pass(pass); else Fail_(fail);
    }

    private void Add(string s)
    {
        _lines.Add(s);
        Console.WriteLine(s);
    }

    public void Save(string path)
    {
        Add($"\n===== 合计 [OK]={Ok} [FAIL]={Fail} =====");
        File.WriteAllText(path, string.Join("\n", _lines), new System.Text.UTF8Encoding(false));
    }

    public string Text => string.Join("\n", _lines);
}

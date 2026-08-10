namespace NRftWManagerUI.Core;

internal sealed class ValueChangeDetector
{
    private readonly GameSession _session;
    private List<long> _candidates = new();

    public ValueChangeDetector(GameSession session)
    {
        _session = session;
    }

    public int CandidateCount => _candidates.Count;
    public IReadOnlyList<long> Candidates => _candidates;

    public string Start(int visibleValue)
    {
        _candidates = _session.FindInt32(visibleValue).ToList();
        return _candidates.Count == 200000
            ? "初扫达到 200,000 个候选上限。请用更独特的数值后重新开始。"
            : $"初扫完成：{_candidates.Count:N0} 个候选。现在只让目标数值变化一次，再输入新数值并筛选。";
    }

    public string Filter(int newVisibleValue)
    {
        if (_candidates.Count == 0)
        {
            return "没有可筛选的候选。请先做一次初扫。";
        }

        _candidates = _session.FilterInt32(_candidates, newVisibleValue).ToList();
        return $"筛选完成：剩余 {_candidates.Count:N0} 个候选。";
    }

    public string BuildReport()
    {
        if (_candidates.Count == 0)
        {
            return "没有候选地址。";
        }

        var lines = _candidates.Take(100).Select(address =>
        {
            var value = _session.TryReadInt32(address, out var current) ? current.ToString() : "读取失败";
            return $"0x{address:X} = {value}";
        });

        var suffix = _candidates.Count > 100 ? $"\n… 仅显示前 100 / {_candidates.Count:N0} 个候选。" : string.Empty;
        return string.Join(Environment.NewLine, lines) + suffix;
    }
}

using System.Text;

namespace Madorin.AI.Runtime.MConsole.LiveHost;

/// <summary>一帧里输出区和事件区各自要显示的行。</summary>
public sealed record DebugFrame(
    IReadOnlyList<string> OutputLines,
    IReadOnlyList<string> EventLines,
    string Status);

/// <summary>保存模型原文和事件流水，并切出当前窗口能放下的尾部。</summary>
public sealed class DebugTranscript
{
    private readonly StringBuilder _output = new();
    private readonly List<string> _events = [];
    private bool _inReasoning;
    private string? _aggregateType;
    private DateTimeOffset _aggregateStarted;
    private long _aggregateFirstGsn;
    private long _aggregateLastGsn;
    private int _aggregateCount;
    private int _aggregateCharacters;

    public object Gate { get; } = new();

    public string Status { get; set; } = string.Empty;

    public void ClearOutput()
    {
        lock (Gate)
        {
            _output.Clear();
            _inReasoning = false;
        }
    }

    public void Append(DebugOutputChannel channel, string? text)
    {
        if (string.IsNullOrEmpty(text) || channel == DebugOutputChannel.None)
        {
            return;
        }

        lock (Gate)
        {
            switch (channel)
            {
                case DebugOutputChannel.Assistant:
                    FinishReasoning();
                    _output.Append(text);
                    break;
                case DebugOutputChannel.Reasoning:
                    if (!_inReasoning)
                    {
                        _output.Append("\n[思考]\n");
                        _inReasoning = true;
                    }

                    _output.Append(text);
                    break;
                case DebugOutputChannel.Notice:
                    FinishReasoning();
                    if (_output.Length > 0 && _output[^1] != '\n')
                    {
                        _output.Append('\n');
                    }

                    _output.Append(text).Append('\n');
                    break;
            }
        }
    }

    /// <summary>
    /// 记录一条事件。连续的思考或输出增量合并成同一行，只累计次数、字数和序号范围。
    /// </summary>
    public void AddEvent(
        string messageType,
        long gsn,
        DateTimeOffset timestamp,
        string line,
        int characters,
        bool aggregate)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(messageType);
        lock (Gate)
        {
            if (aggregate && _aggregateType == messageType && _events.Count > 0)
            {
                _aggregateLastGsn = gsn;
                _aggregateCount++;
                _aggregateCharacters += characters;
                _events[^1] = FormatAggregate(messageType);
                return;
            }

            if (!aggregate)
            {
                _aggregateType = null;
                _events.Add(line);
                return;
            }

            _aggregateType = messageType;
            _aggregateStarted = timestamp;
            _aggregateFirstGsn = gsn;
            _aggregateLastGsn = gsn;
            _aggregateCount = 1;
            _aggregateCharacters = characters;
            _events.Add(FormatAggregate(messageType));
        }
    }

    public void EndTurn()
    {
        lock (Gate)
        {
            _aggregateType = null;
            FinishReasoning();
            if (_output.Length > 0 && _output[^1] != '\n')
            {
                _output.Append('\n');
            }
        }
    }

    private string FormatAggregate(string messageType)
    {
        var range = _aggregateFirstGsn == _aggregateLastGsn
            ? _aggregateFirstGsn.ToString()
            : $"{_aggregateFirstGsn}-{_aggregateLastGsn}";
        return $"{_aggregateStarted:HH:mm:ss} gsn={range} {messageType} ×{_aggregateCount} +{_aggregateCharacters}";
    }

    public DebugFrame Capture(int outputRows, int eventRows, int width)
    {
        lock (Gate)
        {
            return new DebugFrame(
                Tail(Wrap(_output.ToString(), width), outputRows),
                Tail(_events, eventRows),
                Fit(Status, width));
        }
    }

    public string OutputText
    {
        get
        {
            lock (Gate)
            {
                return _output.ToString();
            }
        }
    }

    public IReadOnlyList<string> Events
    {
        get
        {
            lock (Gate)
            {
                return _events.ToArray();
            }
        }
    }

    private void FinishReasoning()
    {
        if (!_inReasoning)
        {
            return;
        }

        _inReasoning = false;
        if (_output.Length > 0 && _output[^1] != '\n')
        {
            _output.Append('\n');
        }
    }

    private static IReadOnlyList<string> Tail(List<string> lines, int rows)
    {
        if (rows <= 0 || lines.Count == 0)
        {
            return [];
        }

        return lines.Count <= rows
            ? lines.ToArray()
            : lines.GetRange(lines.Count - rows, rows);
    }

    private static List<string> Wrap(string text, int width)
    {
        var rows = new List<string>();
        if (text.Length == 0)
        {
            return rows;
        }

        var limit = Math.Max(8, width);
        foreach (var raw in text.ReplaceLineEndings("\n").Split('\n'))
        {
            if (raw.Length == 0)
            {
                rows.Add(string.Empty);
                continue;
            }

            for (var index = 0; index < raw.Length;)
            {
                var take = Math.Min(limit, raw.Length - index);
                rows.Add(raw.Substring(index, take));
                index += take;
            }
        }

        return rows;
    }

    private static string Fit(string text, int width)
    {
        var limit = Math.Max(8, width);
        var compact = text.ReplaceLineEndings(" ");
        return compact.Length <= limit ? compact : compact[..limit];
    }
}

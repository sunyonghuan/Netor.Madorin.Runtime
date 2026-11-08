namespace Madorin.AI.Runtime.Cli.Config;

/// <summary>交互输入一次按键后的结果。</summary>
internal enum InteractiveInputStatus
{
    /// <summary>缓冲区和光标都没有变化。</summary>
    Unchanged = 0,

    /// <summary>文本或光标变了，终端需要重画。</summary>
    Changed = 1,

    /// <summary>用户发送当前输入。</summary>
    Submitted = 2,
}

/// <summary>维护对话输入的文本、光标和本次进程内的命令历史。</summary>
internal sealed class InteractiveLineEditor
{
    private readonly List<string> _history;
    private readonly List<string> _lines = [string.Empty];
    private readonly List<string> _savedDraft = [];
    private int _row;
    private int _column;
    private int _goalColumn;
    private int _historyIndex = -1;
    private int _savedRow;
    private int _savedColumn;
    private int _savedGoalColumn;
    private bool _hasSavedDraft;

    /// <summary>创建一个编辑器。<paramref name="history"/> 在多次读取之间共用。</summary>
    public InteractiveLineEditor(List<string> history)
    {
        ArgumentNullException.ThrowIfNull(history);
        _history = history;
    }

    /// <summary>当前各行文本。</summary>
    public IReadOnlyList<string> Lines => _lines;

    /// <summary>光标所在行，从 0 开始。</summary>
    public int Row => _row;

    /// <summary>光标在当前行中的字符位置。</summary>
    public int Column => _column;

    /// <summary>用换行符拼起来的当前输入。</summary>
    public string Text => string.Join('\n', _lines);

    /// <summary>处理一次按键。方向键移动光标，第一行再按上键才切换历史。</summary>
    public InteractiveInputStatus Apply(
        ConsoleKeyInfo key,
        bool moreKeysQueued,
        long elapsedMilliseconds)
    {
        if (key.Key == ConsoleKey.Enter || key.KeyChar is '\r' or '\n')
        {
            return ApplyEnter(key, moreKeysQueued, elapsedMilliseconds);
        }

        return key.Key switch
        {
            ConsoleKey.Backspace => ApplyBackspace(),
            ConsoleKey.Delete => ApplyDelete(),
            ConsoleKey.LeftArrow => MoveLeft(),
            ConsoleKey.RightArrow => MoveRight(),
            ConsoleKey.UpArrow => MoveVertical(-1),
            ConsoleKey.DownArrow => MoveVertical(1),
            ConsoleKey.Home => MoveToColumn(0),
            ConsoleKey.End => MoveToColumn(_lines[_row].Length),
            _ => InsertPrintable(key.KeyChar),
        };
    }

    private InteractiveInputStatus ApplyEnter(
        ConsoleKeyInfo key,
        bool moreKeysQueued,
        long elapsedMilliseconds)
    {
        var shift = (key.Modifiers & ConsoleModifiers.Shift) != 0;
        if (InteractiveConsoleInput.IsSubmitEnter(shift, moreKeysQueued, elapsedMilliseconds))
        {
            Remember(Text);
            return InteractiveInputStatus.Submitted;
        }

        var line = _lines[_row];
        var tail = line[_column..];
        _lines[_row] = line[.._column];
        _lines.Insert(_row + 1, tail);
        _row++;
        _column = 0;
        _goalColumn = 0;
        return InteractiveInputStatus.Changed;
    }

    private InteractiveInputStatus ApplyBackspace()
    {
        if (_column > 0)
        {
            _column--;
            _lines[_row] = _lines[_row].Remove(_column, 1);
            _goalColumn = _column;
            return InteractiveInputStatus.Changed;
        }

        if (_row == 0)
        {
            return InteractiveInputStatus.Unchanged;
        }

        var current = _lines[_row];
        _row--;
        _column = _lines[_row].Length;
        _goalColumn = _column;
        _lines[_row] += current;
        _lines.RemoveAt(_row + 1);
        return InteractiveInputStatus.Changed;
    }

    private InteractiveInputStatus ApplyDelete()
    {
        var line = _lines[_row];
        if (_column < line.Length)
        {
            _lines[_row] = line.Remove(_column, 1);
            _goalColumn = _column;
            return InteractiveInputStatus.Changed;
        }

        if (_row >= _lines.Count - 1)
        {
            return InteractiveInputStatus.Unchanged;
        }

        _lines[_row] = line + _lines[_row + 1];
        _lines.RemoveAt(_row + 1);
        _goalColumn = _column;
        return InteractiveInputStatus.Changed;
    }

    private InteractiveInputStatus MoveLeft()
    {
        if (_column > 0)
        {
            return MoveToColumn(_column - 1);
        }

        if (_row == 0)
        {
            return InteractiveInputStatus.Unchanged;
        }

        _row--;
        _column = _lines[_row].Length;
        _goalColumn = _column;
        return InteractiveInputStatus.Changed;
    }

    private InteractiveInputStatus MoveRight()
    {
        if (_column < _lines[_row].Length)
        {
            return MoveToColumn(_column + 1);
        }

        if (_row >= _lines.Count - 1)
        {
            return InteractiveInputStatus.Unchanged;
        }

        _row++;
        _column = 0;
        _goalColumn = 0;
        return InteractiveInputStatus.Changed;
    }

    /// <summary>行内先上下移动。到顶或到底之后才翻历史。</summary>
    private InteractiveInputStatus MoveVertical(int direction)
    {
        var nextRow = _row + direction;
        if ((uint)nextRow < (uint)_lines.Count)
        {
            _row = nextRow;
            _column = Math.Min(_goalColumn, _lines[_row].Length);
            return InteractiveInputStatus.Changed;
        }

        return MoveHistory(direction);
    }

    private InteractiveInputStatus MoveToColumn(int column)
    {
        column = Math.Clamp(column, 0, _lines[_row].Length);
        if (column == _column)
        {
            return InteractiveInputStatus.Unchanged;
        }

        _column = column;
        _goalColumn = column;
        return InteractiveInputStatus.Changed;
    }

    private InteractiveInputStatus InsertPrintable(char value)
    {
        if (value == '\0' || char.IsControl(value))
        {
            return InteractiveInputStatus.Unchanged;
        }

        var line = _lines[_row];
        _lines[_row] = line.Insert(_column, value.ToString());
        _column++;
        _goalColumn = _column;
        return InteractiveInputStatus.Changed;
    }

    /// <summary>空白输入不记。连续相同的输入只留一条。历史项本身不被修改。</summary>
    private void Remember(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        if (_history.Count > 0 && string.Equals(_history[^1], text, StringComparison.Ordinal))
        {
            return;
        }

        _history.Add(text);
    }

    private InteractiveInputStatus MoveHistory(int direction)
    {
        if (_history.Count == 0)
        {
            return InteractiveInputStatus.Unchanged;
        }

        var index = _historyIndex < 0 ? _history.Count : _historyIndex;
        var next = index + direction;
        if (next < 0 || next > _history.Count)
        {
            return InteractiveInputStatus.Unchanged;
        }

        if (!_hasSavedDraft)
        {
            _savedDraft.Clear();
            _savedDraft.AddRange(_lines);
            _savedRow = _row;
            _savedColumn = _column;
            _savedGoalColumn = _goalColumn;
            _hasSavedDraft = true;
        }

        if (next == _history.Count)
        {
            RestoreDraft();
            return InteractiveInputStatus.Changed;
        }

        _historyIndex = next;
        Load(_history[next]);
        return InteractiveInputStatus.Changed;
    }

    private void RestoreDraft()
    {
        _lines.Clear();
        if (_savedDraft.Count == 0)
        {
            _lines.Add(string.Empty);
        }
        else
        {
            _lines.AddRange(_savedDraft);
        }

        _row = Math.Clamp(_savedRow, 0, _lines.Count - 1);
        _column = Math.Clamp(_savedColumn, 0, _lines[_row].Length);
        _goalColumn = _savedGoalColumn;
        _historyIndex = -1;
        _hasSavedDraft = false;
        _savedDraft.Clear();
    }

    private void Load(string text)
    {
        _lines.Clear();
        _lines.AddRange(text.Split('\n'));

        _row = _lines.Count - 1;
        _column = _lines[_row].Length;
        _goalColumn = _column;
    }
}

using Madorin.AI.Runtime.Cli.Config;

namespace Madorin.AI.Runtime.EndToEnd.Tests;

[TestClass]
public sealed class InteractiveLineEditorTests
{
    private const int NormalGap = 1_000;

    [TestMethod]
    public void InsertAndBackspace_InMiddle_KeepCursor()
    {
        var editor = new InteractiveLineEditor([]);
        Type(editor, "ac");
        Press(editor, ConsoleKey.LeftArrow);
        Type(editor, "b");

        Assert.AreEqual("abc", editor.Text);
        Assert.AreEqual(2, editor.Column);

        Press(editor, ConsoleKey.Backspace);
        Assert.AreEqual("ac", editor.Text);
        Assert.AreEqual(1, editor.Column);
    }

    [TestMethod]
    public void Delete_InMiddle_RemovesCharacterAtCursor()
    {
        var editor = new InteractiveLineEditor([]);
        Type(editor, "abc");
        Press(editor, ConsoleKey.LeftArrow);
        Press(editor, ConsoleKey.LeftArrow);
        Press(editor, ConsoleKey.Delete);

        Assert.AreEqual("ac", editor.Text);
        Assert.AreEqual(1, editor.Column);
    }

    [TestMethod]
    public void HorizontalArrows_CrossLines()
    {
        var editor = new InteractiveLineEditor([]);
        Type(editor, "ab");
        NewLine(editor);
        Assert.AreEqual(InteractiveInputStatus.Changed, Press(editor, ConsoleKey.LeftArrow));
        Assert.AreEqual(0, editor.Row);
        Assert.AreEqual(2, editor.Column);

        Assert.AreEqual(InteractiveInputStatus.Changed, Press(editor, ConsoleKey.RightArrow));
        Assert.AreEqual(1, editor.Row);
        Assert.AreEqual(0, editor.Column);
        Assert.AreEqual("ab\n", editor.Text);
    }

    [TestMethod]
    public void ShiftEnter_SplitsAtCursor_AndPlainEnterSubmits()
    {
        var history = new List<string>();
        var editor = new InteractiveLineEditor(history);
        Type(editor, "abcd");
        Press(editor, ConsoleKey.LeftArrow);
        Press(editor, ConsoleKey.LeftArrow);
        Assert.AreEqual(InteractiveInputStatus.Changed, NewLine(editor));
        Assert.AreEqual("ab\ncd", editor.Text);
        Assert.IsEmpty(history);

        Type(editor, "X");
        Assert.AreEqual(InteractiveInputStatus.Submitted, Submit(editor));
        Assert.AreEqual("ab\nXcd", editor.Text);
        Assert.HasCount(1, history);
        Assert.AreEqual("ab\nXcd", history[0]);
    }

    [TestMethod]
    public void PasteEnter_InsertsLineInsteadOfSubmitting()
    {
        var editor = new InteractiveLineEditor([]);
        Type(editor, "ab");
        var queued = editor.Apply(EnterKey(shift: false), moreKeysQueued: true, NormalGap);
        var burst = editor.Apply(
            EnterKey(shift: false),
            moreKeysQueued: false,
            InteractiveConsoleInput.PasteBurstMilliseconds - 1);

        Assert.AreEqual(InteractiveInputStatus.Changed, queued);
        Assert.AreEqual(InteractiveInputStatus.Changed, burst);
        Assert.AreEqual("ab\n\n", editor.Text);
    }

    [TestMethod]
    public void BackspaceAndDelete_AtLineBoundary_JoinLines()
    {
        var editor = new InteractiveLineEditor([]);
        Type(editor, "ab");
        NewLine(editor);
        Type(editor, "cd");
        Press(editor, ConsoleKey.Home);
        Press(editor, ConsoleKey.Backspace);
        Assert.AreEqual("abcd", editor.Text);
        Assert.AreEqual(0, editor.Row);
        Assert.AreEqual(2, editor.Column);

        NewLine(editor);
        Press(editor, ConsoleKey.UpArrow);
        Press(editor, ConsoleKey.End);
        Press(editor, ConsoleKey.Delete);
        Assert.AreEqual("abcd", editor.Text);
    }

    [TestMethod]
    public void UpAndDown_RecallHistoryThenRestoreDraft()
    {
        var history = new List<string>();
        SubmitText(history, "first");
        SubmitText(history, "second");
        var editor = new InteractiveLineEditor(history);
        Type(editor, "draft");
        Press(editor, ConsoleKey.LeftArrow);

        Assert.AreEqual(InteractiveInputStatus.Changed, Press(editor, ConsoleKey.UpArrow));
        Assert.AreEqual("second", editor.Text);
        Assert.AreEqual(6, editor.Column);
        Press(editor, ConsoleKey.UpArrow);
        Assert.AreEqual("first", editor.Text);
        Assert.AreEqual(InteractiveInputStatus.Unchanged, Press(editor, ConsoleKey.UpArrow));
        Assert.AreEqual("first", editor.Text);

        Press(editor, ConsoleKey.DownArrow);
        Assert.AreEqual("second", editor.Text);
        Press(editor, ConsoleKey.DownArrow);
        Assert.AreEqual("draft", editor.Text);
        Assert.AreEqual(4, editor.Column);
    }

    [TestMethod]
    public void UpInsideMultiline_MovesLineBeforeHistory()
    {
        var history = new List<string>();
        SubmitText(history, "old");
        var editor = new InteractiveLineEditor(history);
        Type(editor, "ab");
        NewLine(editor);
        Type(editor, "cd");

        Press(editor, ConsoleKey.UpArrow);
        Assert.AreEqual("ab\ncd", editor.Text);
        Assert.AreEqual(0, editor.Row);
        Assert.AreEqual(2, editor.Column);

        Press(editor, ConsoleKey.UpArrow);
        Assert.AreEqual("old", editor.Text);
        Assert.HasCount(1, history);
        Assert.AreEqual("old", history[0]);
    }

    [TestMethod]
    public void VerticalMove_RemembersColumnAcrossShortLines()
    {
        var editor = new InteractiveLineEditor([]);
        Type(editor, "ab");
        NewLine(editor);
        Type(editor, "hello");
        Press(editor, ConsoleKey.UpArrow);
        Assert.AreEqual(0, editor.Row);
        Assert.AreEqual(2, editor.Column);

        Press(editor, ConsoleKey.DownArrow);
        Assert.AreEqual(1, editor.Row);
        Assert.AreEqual(5, editor.Column);
    }

    [TestMethod]
    public void EditingRecalledHistory_DoesNotChangeStoredEntry()
    {
        var history = new List<string>();
        SubmitText(history, "hello");
        var editor = new InteractiveLineEditor(history);
        Press(editor, ConsoleKey.UpArrow);
        Press(editor, ConsoleKey.LeftArrow);
        Type(editor, "X");

        Assert.AreEqual("hellXo", editor.Text);
        Assert.AreEqual("hello", history[0]);
        Assert.AreEqual(InteractiveInputStatus.Submitted, Submit(editor));
        Assert.HasCount(2, history);
        Assert.AreEqual("hello", history[0]);
        Assert.AreEqual("hellXo", history[1]);
    }

    [TestMethod]
    public void Submit_SkipsWhitespaceAndConsecutiveDuplicate()
    {
        var history = new List<string>();
        var blank = new InteractiveLineEditor(history);
        Type(blank, "   ");
        Assert.AreEqual(InteractiveInputStatus.Submitted, Submit(blank));
        Assert.IsEmpty(history);

        SubmitText(history, "echo");
        SubmitText(history, "echo");
        SubmitText(history, "next");
        Assert.HasCount(2, history);
        Assert.AreEqual("echo", history[0]);
        Assert.AreEqual("next", history[1]);
    }

    [TestMethod]
    public void Layout_PlacesCursorAndWrapsWideText()
    {
        var simple = InteractiveInputLayout.Build(["hi"], 0, 1, "> ", 80);
        Assert.AreEqual("> ", simple.Rows[0].PromptText);
        Assert.AreEqual("hi", simple.Rows[0].UserText);
        Assert.AreEqual(0, simple.CursorRow);
        Assert.AreEqual(3, simple.CursorColumn);

        var wrapped = InteractiveInputLayout.Build(["abcd"], 0, 4, "> ", 6);
        Assert.HasCount(2, wrapped.Rows);
        Assert.AreEqual("> ", wrapped.Rows[0].PromptText);
        Assert.AreEqual("abc", wrapped.Rows[0].UserText);
        Assert.AreEqual(string.Empty, wrapped.Rows[1].PromptText);
        Assert.AreEqual("d", wrapped.Rows[1].UserText);
        Assert.AreEqual(1, wrapped.CursorRow);
        Assert.AreEqual(1, wrapped.CursorColumn);

        var chinese = InteractiveInputLayout.Build(["你好"], 0, 1, "> ", 6);
        Assert.AreEqual("你", chinese.Rows[0].UserText);
        Assert.AreEqual("好", chinese.Rows[1].UserText);
        Assert.AreEqual(0, chinese.CursorRow);
        Assert.AreEqual(4, chinese.CursorColumn);

        var multiline = InteractiveInputLayout.Build(["hello", "!"], 1, 1, "> ", 80);
        Assert.AreEqual("hello", multiline.Rows[0].UserText);
        Assert.AreEqual("!", multiline.Rows[1].UserText);
        Assert.AreEqual(1, multiline.CursorRow);
        Assert.AreEqual(3, multiline.CursorColumn);
    }

    private static void SubmitText(List<string> history, string text)
    {
        var editor = new InteractiveLineEditor(history);
        Type(editor, text);
        Assert.AreEqual(InteractiveInputStatus.Submitted, Submit(editor));
    }

    private static void Type(InteractiveLineEditor editor, string text)
    {
        foreach (var character in text)
        {
            var status = editor.Apply(
                new ConsoleKeyInfo(character, ConsoleKey.A, false, false, false),
                moreKeysQueued: false,
                NormalGap);
            Assert.AreEqual(InteractiveInputStatus.Changed, status);
        }
    }

    private static InteractiveInputStatus Press(InteractiveLineEditor editor, ConsoleKey key) =>
        editor.Apply(new ConsoleKeyInfo('\0', key, false, false, false), moreKeysQueued: false, NormalGap);

    private static InteractiveInputStatus NewLine(InteractiveLineEditor editor) =>
        editor.Apply(EnterKey(shift: true), moreKeysQueued: false, NormalGap);

    private static InteractiveInputStatus Submit(InteractiveLineEditor editor) =>
        editor.Apply(EnterKey(shift: false), moreKeysQueued: false, NormalGap);

    private static ConsoleKeyInfo EnterKey(bool shift) =>
        new('\r', ConsoleKey.Enter, shift, alt: false, control: false);
}

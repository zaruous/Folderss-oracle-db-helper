using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using MyPlugin;
using Xunit;

namespace MyPlugin.Tests
{
    /// <summary>WorkspaceLogic: SQL 작업 영역의 탭 이름·줄 표시·SELECT 문장·상태줄·메시지·TSV 규칙.</summary>
    public class WorkspaceLogicTests
    {
        // ---------- 탭 ----------

        [Fact]
        public void NextTabNumber_SkipsUsedTitlesFromStart()
        {
            Assert.Equal(1, WorkspaceLogic.NextTabNumber(new string[0], 1));
            Assert.Equal(3, WorkspaceLogic.NextTabNumber(new[] { "SQL 1", "SQL 2" }, 1));
            // 닫은 탭 번호(2)는 호출자가 start를 늘려 다시 쓰지 않는다
            Assert.Equal(4, WorkspaceLogic.NextTabNumber(new[] { "SQL 1", "SQL 3" }, 3));
            Assert.Equal(5, WorkspaceLogic.NextTabNumber(new[] { "SQL 4", null }, 4));
            Assert.Equal(1, WorkspaceLogic.NextTabNumber(null, 0));
            Assert.Equal("SQL 12", WorkspaceLogic.TabTitle(12));
        }

        [Fact]
        public void TabsToClose_ByScope()
        {
            // 탭 5개(0~4) 중 2번에서 메뉴를 열었다
            Assert.Equal(new[] { 2 }, WorkspaceLogic.TabsToClose(5, 2, TabCloseScope.This));
            Assert.Equal(new[] { 0, 1, 3, 4 }, WorkspaceLogic.TabsToClose(5, 2, TabCloseScope.Others));
            Assert.Equal(new[] { 0, 1 }, WorkspaceLogic.TabsToClose(5, 2, TabCloseScope.Left));
            Assert.Equal(new[] { 3, 4 }, WorkspaceLogic.TabsToClose(5, 2, TabCloseScope.Right));
            // 양 끝: 왼쪽·오른쪽에 닫을 탭이 없다
            Assert.Empty(WorkspaceLogic.TabsToClose(5, 0, TabCloseScope.Left));
            Assert.Empty(WorkspaceLogic.TabsToClose(5, 4, TabCloseScope.Right));
            // 탭이 하나뿐이면 무엇도 닫지 않는다(작업 영역은 탭을 하나 이상 둔다)
            foreach (TabCloseScope scope in Enum.GetValues(typeof(TabCloseScope)))
                Assert.Empty(WorkspaceLogic.TabsToClose(1, 0, scope));
            // 범위 밖
            Assert.Empty(WorkspaceLogic.TabsToClose(5, -1, TabCloseScope.Others));
            Assert.Empty(WorkspaceLogic.TabsToClose(5, 5, TabCloseScope.This));
        }

        [Fact]
        public void NewTabText_NamesTargetOrNone()
        {
            Assert.Equal("-- 대상: 개발\n", WorkspaceLogic.NewTabText("개발"));
            Assert.Equal("-- 대상: (없음)\n", WorkspaceLogic.NewTabText(null));
        }

        [Fact]
        public void TargetItemText_AddsConnectionAndReadOnlyMarks()
        {
            Assert.Equal("개발", WorkspaceLogic.TargetItemText("개발", true, false));
            Assert.Equal("운영 (연결 안 됨) · 읽기 전용", WorkspaceLogic.TargetItemText("운영", false, true));
            Assert.Equal("운영 · 읽기 전용", WorkspaceLogic.TargetItemText("운영", true, true));
        }

        // ---------- 편집기 ----------

        [Theory]
        [InlineData("", 1)]
        [InlineData("SELECT 1", 1)]
        [InlineData("a\nb", 2)]
        [InlineData("a\r\nb", 2)]
        [InlineData("a\rb", 2)]
        [InlineData("a\r\n", 2)]
        [InlineData("a\r\n\r\nb\nc\rd", 5)]
        public void CountLines_TreatsCrLfLfCrAsOneBreak(string text, int expected)
        {
            Assert.Equal(expected, WorkspaceLogic.CountLines(text));
        }

        [Fact]
        public void LineNumbers_OnePerLine()
        {
            Assert.Equal("1\n2\n3", WorkspaceLogic.LineNumbers(3));
            Assert.Equal("1", WorkspaceLogic.LineNumbers(0));
        }

        [Theory]
        [InlineData("SELECT 1", 0, 1, 1)]
        [InlineData("SELECT 1", 6, 1, 7)]
        [InlineData("a\r\nbcd", 3, 2, 1)]
        [InlineData("a\r\nbcd", 5, 2, 3)]
        [InlineData("a\nb\nc", 4, 3, 1)]
        [InlineData("a\rbc", 3, 2, 2)]
        [InlineData("\tx", 2, 1, 3)]
        [InlineData("ab", 99, 1, 3)]
        public void CaretLineColumn_IsOneBased(string text, int caret, int line, int column)
        {
            int l, c;
            WorkspaceLogic.CaretLineColumn(text, caret, out l, out c);
            Assert.Equal(line, l);
            Assert.Equal(column, c);
        }

        [Fact]
        public void CaretLineColumn_CaretInsideCrLfStaysOnLine()
        {
            int l, c;
            WorkspaceLogic.CaretLineColumn("ab\r\ncd", 3, out l, out c);
            Assert.Equal(1, l);
            Assert.Equal(4, c);
        }

        [Fact]
        public void CursorInfo_ShowsSelectionCountingCrLfOnce()
        {
            Assert.Equal("줄 1, 열 1", WorkspaceLogic.CursorInfo("", 0, 0, 0));
            const string text = "SELECT *\r\n  FROM emp";
            Assert.Equal("줄 2, 열 3", WorkspaceLogic.CursorInfo(text, 12, 12, 0));
            // "*\r\n  F" = 6글자(CRLF 하나로)
            Assert.Equal("줄 1, 열 8 · 선택 5자", WorkspaceLogic.CursorInfo(text, 7, 7, 6));
            Assert.Equal(2, WorkspaceLogic.SelectedCharCount("a\r\nb", 0, 3));
            var longText = new string('\n', 1500) + "x";
            Assert.Equal("줄 1501, 열 2 · 선택 1,501자", WorkspaceLogic.CursorInfo(longText, longText.Length, 0, longText.Length));
            Assert.Equal(0, WorkspaceLogic.SelectedCharCount("abc", 5, 3));
        }

        [Fact]
        public void NormalizeNewlines_UsesOneLineBreakStyle()
        {
            Assert.Equal("a\r\nb\r\nc\r\nd", WorkspaceLogic.NormalizeNewlines("a\nb\r\nc\rd", "\r\n"));
            Assert.Equal("", WorkspaceLogic.NormalizeNewlines(null, "\r\n"));
            Assert.Equal("x", WorkspaceLogic.NormalizeNewlines("x", "\r\n"));
        }

        // ---------- 문장 ----------

        [Fact]
        public void FirstLine_SkipsBlankLinesAndTruncates()
        {
            Assert.Equal("SELECT e.empno", WorkspaceLogic.FirstLine("\r\n   SELECT e.empno\r\n  FROM emp e"));
            Assert.Equal("a b", WorkspaceLogic.FirstLine("a\tb"));
            Assert.Equal("", WorkspaceLogic.FirstLine(" \n \n"));
            Assert.Equal("", WorkspaceLogic.FirstLine(null));
            Assert.Equal("abcde…", WorkspaceLogic.FirstLine("abcdefgh", 5));
            // 서로게이트 쌍을 반으로 자르지 않는다
            Assert.Equal("ab…", WorkspaceLogic.FirstLine("ab\U0001F600cd", 3));
        }

        [Theory]
        [InlineData("SELECT * FROM emp", true)]
        [InlineData("WITH x AS (SELECT 1 FROM dual) SELECT * FROM x", true)]
        [InlineData("SELECT * FROM emp FOR UPDATE", false)]
        [InlineData("UPDATE emp SET sal = 1 WHERE empno = 1", false)]
        [InlineData("DELETE FROM emp WHERE empno = 1", false)]
        [InlineData("DROP TABLE emp", false)]
        [InlineData("BEGIN NULL; END;", false)]
        [InlineData("EXEC dbms_output.put_line('x')", false)]
        [InlineData("ALTER SESSION SET nls_date_format = 'YYYY-MM-DD'", false)]
        [InlineData("COMMIT", true)]
        [InlineData("ROLLBACK", true)]
        [InlineData("ROLLBACK TO SAVEPOINT s1", false)]
        [InlineData("SAVEPOINT s1", false)]
        [InlineData("LOCK TABLE emp IN EXCLUSIVE MODE", false)]
        public void AllowedOnReadOnly_OnlyQueriesAndTransactionEnds(string sql, bool allowed)
        {
            Assert.Equal(allowed, WorkspaceLogic.AllowedOnReadOnly(SqlScript.Parse(sql)));
        }

        [Fact]
        public void AllowedOnReadOnly_NullIsNotAllowed()
        {
            Assert.False(WorkspaceLogic.AllowedOnReadOnly(null));
        }

        [Theory]
        [InlineData("EMP", "EMP")]
        [InlineData("EMP_2$#", "EMP_2$#")]
        [InlineData("emp", "\"emp\"")]
        [InlineData("MyTable", "\"MyTable\"")]
        [InlineData("MY TABLE", "\"MY TABLE\"")]
        [InlineData("A\"B", "\"A\"\"B\"")]
        [InlineData("1ABC", "\"1ABC\"")]
        [InlineData("_ABC", "\"_ABC\"")]
        [InlineData("사원", "\"사원\"")]
        [InlineData("DATE", "\"DATE\"")]
        [InlineData("USER", "\"USER\"")]
        [InlineData("", "\"\"")]
        public void QuoteIdentifier_QuotesWhenNotSimpleUppercaseOrReserved(string name, string expected)
        {
            Assert.Equal(expected, WorkspaceLogic.QuoteIdentifier(name));
        }

        [Fact]
        public void SelectStatement_QualifiesAndQuotes()
        {
            Assert.Equal("SELECT *\n  FROM SCOTT.EMP;\n\n", WorkspaceLogic.SelectStatement("SCOTT", "EMP"));
            Assert.Equal("SELECT *\n  FROM \"app\".\"Order Items\";\n\n", WorkspaceLogic.SelectStatement("app", "Order Items"));
            Assert.Equal("SELECT *\n  FROM EMP;\n\n", WorkspaceLogic.SelectStatement(null, "EMP"));
            Assert.Equal("SCOTT.\"emp\"", WorkspaceLogic.QualifiedName("SCOTT", "emp"));
        }

        [Fact]
        public void SelectStatement_IsOneStatementAboveExistingText()
        {
            var script = WorkspaceLogic.SelectStatement("SCOTT", "EMP") + "UPDATE emp SET sal = 1 WHERE empno = 7839";
            var statements = SqlScript.Split(script);
            Assert.Equal(2, statements.Count);
            Assert.Equal("SELECT *\n  FROM SCOTT.EMP", statements[0].Text);
            Assert.Equal(SqlKind.Query, SqlScript.AtCaret(script, 0).Kind);
        }

        [Fact]
        public void HistoryInsertText_EndsStatementsWithSemicolonAndBlocksWithSlash()
        {
            Assert.Equal("SELECT 1 FROM dual;\n\n", WorkspaceLogic.HistoryInsertText("SELECT 1 FROM dual"));
            Assert.Equal("BEGIN p(1); END;\n/\n\n", WorkspaceLogic.HistoryInsertText("BEGIN p(1); END;"));
            Assert.Equal("CREATE OR REPLACE PROCEDURE p IS BEGIN NULL; END;\n/\n\n",
                WorkspaceLogic.HistoryInsertText("CREATE OR REPLACE PROCEDURE p IS BEGIN NULL; END;"));
        }

        [Fact]
        public void HistoryInsertText_BlockDoesNotSwallowFollowingStatements()
        {
            // EXEC p(1)은 실행할 때 "BEGIN p(1); END;"로 바뀌어 기록된다
            var script = WorkspaceLogic.HistoryInsertText("BEGIN p(1); END;") + "SELECT * FROM emp";
            var statements = SqlScript.Split(script);
            Assert.Equal(2, statements.Count);
            Assert.Equal(SqlKind.PlSql, statements[0].Kind);
            Assert.Equal("SELECT * FROM emp", statements[1].Text);
        }

        // ---------- 시간·메시지 ----------

        [Fact]
        public void Seconds_UseInvariantCulture()
        {
            var saved = Thread.CurrentThread.CurrentCulture;
            try
            {
                Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");
                Assert.Equal("0.35", WorkspaceLogic.Seconds(TimeSpan.FromMilliseconds(350)));
                Assert.Equal("1.2", WorkspaceLogic.SecondsShort(TimeSpan.FromMilliseconds(1234)));
                Assert.Equal("1,234", WorkspaceLogic.Count(1234));
                Assert.Equal("0.00", WorkspaceLogic.Seconds(TimeSpan.FromSeconds(-1)));
            }
            finally
            {
                Thread.CurrentThread.CurrentCulture = saved;
            }
        }

        [Fact]
        public void ExecutedMessage_SummarySecondsFirstLine()
        {
            Assert.Equal("200행 · 더 있음 · 0.35초 · SELECT e.empno, e.ename",
                WorkspaceLogic.ExecutedMessage("200행 · 더 있음", TimeSpan.FromMilliseconds(350), "SELECT e.empno, e.ename\n  FROM emp e"));
        }

        [Fact]
        public void ErrorMessage_AddsSnapshotHintAndFirstLine()
        {
            Assert.Equal("ORA-00942: table or view does not exist · SELECT * FROM nope",
                WorkspaceLogic.ErrorMessage("ORA-00942: table or view does not exist", "SELECT * FROM nope"));
            var snapshot = WorkspaceLogic.FetchErrorMessage("ORA-01555: snapshot too old");
            Assert.StartsWith("다음 행을 가져오지 못했습니다: ORA-01555: snapshot too old — ", snapshot);
            Assert.EndsWith("다시 실행하세요.", snapshot);
            Assert.Equal("실행을 취소했습니다.", WorkspaceLogic.ErrorMessage("실행을 취소했습니다.", null));
        }

        [Fact]
        public void FetchedMessage_RangeAndLastRow()
        {
            Assert.Equal("다음 200행 가져옴 (201–400) · 0.12초",
                WorkspaceLogic.FetchedMessage(200, 201, 400, TimeSpan.FromMilliseconds(120), false));
            Assert.Equal("다음 14행 가져옴 (1,001–1,014) · 0.05초 · 마지막 행",
                WorkspaceLogic.FetchedMessage(14, 1001, 1014, TimeSpan.FromMilliseconds(50), true));
            Assert.Equal("더 가져올 행이 없습니다 (커서 닫힘).", WorkspaceLogic.FetchedMessage(0, 201, 200, TimeSpan.Zero, true));
        }

        [Fact]
        public void Messages_MatchPocWording()
        {
            Assert.Equal("읽기 전용 접속이라 실행하지 않았습니다: UPDATE emp SET sal = 0",
                WorkspaceLogic.ReadOnlyBlockedMessage("UPDATE emp SET sal = 0\nWHERE 1 = 1"));
            Assert.Equal("'SQL 2' 탭의 대상을 운영(으)로 바꿨습니다.", WorkspaceLogic.TargetChangedMessage("SQL 2", "운영"));
            Assert.Equal("'SQL 3' 탭의 대상 접속이 삭제되어 대상을 비웠습니다.", WorkspaceLogic.TargetRemovedMessage("SQL 3"));
            Assert.Equal("SELECT 만듦: SCOTT.EMP", WorkspaceLogic.SelectMadeMessage("SCOTT.EMP", false, "SQL 1", "개발"));
            Assert.Equal("SELECT 만듦: HR.JOBS → 'SQL 4' 탭(대상 운영)", WorkspaceLogic.SelectMadeMessage("HR.JOBS", true, "SQL 4", "운영"));
            Assert.Equal("기록에서 넣음: SELECT 1 FROM dual → 'SQL 2' 탭(대상 개발)",
                WorkspaceLogic.HistoryInsertedMessage("SELECT 1 FROM dual", "SQL 2", "개발"));
            Assert.Equal("1,200행을 TSV로 복사했습니다(머리글 포함).", WorkspaceLogic.CopiedMessage(1200));
        }

        // ---------- 상태줄 ----------

        [Fact]
        public void FetchStatus_OpenCursor()
        {
            var status = WorkspaceLogic.FetchStatus(200, TimeSpan.FromMilliseconds(350), true, false, true);
            Assert.Equal("200행 표시 중  0.35초  아직 더 있음 · 커서 열림  끝의 ; 는 빼고 실행함", status.PlainText);
            Assert.Equal(WorkspaceLogic.StatusAction.FetchNext, status.Action);
            Assert.True(status.ShowsCursor);
            Assert.True(status.Segments[0].Strong);
            Assert.Equal("200행", status.Segments[0].Text);
            Assert.False(status.Segments[1].NewPart);
        }

        [Fact]
        public void FetchStatus_ClosedCursorVariants()
        {
            Assert.Equal("14행 표시 중  0.02초  마지막 행까지 가져옴 · 커서 닫힘",
                WorkspaceLogic.FetchStatus(14, TimeSpan.FromMilliseconds(20), false, false, false).PlainText);
            Assert.Equal("1,000행 표시 중  커서 닫힘",
                WorkspaceLogic.FetchStatus(1000, null, false, true, false).PlainText);
            Assert.Equal("0행 표시 중  커서 닫힘", WorkspaceLogic.FetchStatus(0, null, false, false, false).PlainText);
            Assert.Equal("취소됨  200행 표시 중  커서 닫힘", WorkspaceLogic.FetchStatus(200, null, false, true, false, "취소됨").PlainText);
        }

        [Fact]
        public void FetchButtonText_ShowsCountOrNone()
        {
            Assert.Equal("다음 1,000행 가져오기", WorkspaceLogic.FetchButtonText(true, 1000));
            Assert.Equal("더 가져올 행 없음", WorkspaceLogic.FetchButtonText(false, 200));
        }

        [Fact]
        public void ExecutedStatus_NotesByKind()
        {
            var update = SqlScript.Parse("UPDATE emp SET sal = sal WHERE empno = 1");
            var dml = WorkspaceLogic.ExecutedStatus(update, new ExecuteResult { Kind = SqlKind.Dml, Summary = "3행 변경됨 (커밋 전)" }, TimeSpan.FromMilliseconds(40));
            Assert.Equal("3행 변경됨 (커밋 전)  0.04초  커밋 전에는 다른 세션에 보이지 않습니다. 같은 DB의 다른 탭에는 보입니다(세션 공유).", dml.PlainText);
            Assert.True(dml.Segments[0].Strong);
            Assert.Equal(WorkspaceLogic.StatusAction.None, dml.Action);

            var commit = WorkspaceLogic.ExecutedStatus(SqlScript.Parse("COMMIT"), new ExecuteResult { Kind = SqlKind.Transaction, Summary = "커밋함", TransactionEnded = true }, TimeSpan.Zero);
            Assert.Equal("커밋함  0.00초  같은 DB의 모든 탭에 적용됩니다.", commit.PlainText);

            var ddl = WorkspaceLogic.ExecutedStatus(SqlScript.Parse("CREATE TABLE t (a NUMBER)"), new ExecuteResult { Kind = SqlKind.Ddl, Summary = "실행함 (DDL은 자동 커밋됨)", TransactionEnded = true }, TimeSpan.FromSeconds(1));
            Assert.Equal("실행함 (DDL은 자동 커밋됨)  1.00초", ddl.PlainText);
        }

        [Fact]
        public void ExecutedStatus_WithWarning_IsErrorStatus()
        {
            var create = SqlScript.Parse("CREATE OR REPLACE PROCEDURE p AS BEGIN x; END;");
            var result = new ExecuteResult
            {
                Kind = SqlKind.Ddl,
                Summary = "실행함 — 컴파일 오류 (DDL은 자동 커밋됨)",
                TransactionEnded = true,
                Warning = "만들었지만 컴파일 오류가 있습니다 (ORA-24344)."
            };

            var status = WorkspaceLogic.ExecutedStatus(create, result, TimeSpan.FromMilliseconds(120));

            Assert.Equal("실행함 — 컴파일 오류 (DDL은 자동 커밋됨)  0.12초  오류 — 메시지 탭을 보세요.", status.PlainText);
        }

        [Fact]
        public void ExecutedStatus_PlSqlThatLeftNoTransaction_HasNoUncommittedNote()
        {
            var block = SqlScript.Parse("BEGIN pkg.recalc; END;");

            var ended = WorkspaceLogic.ExecutedStatus(block, new ExecuteResult { Kind = SqlKind.PlSql, Summary = "실행함 (블록 안에서 커밋·롤백됨)", TransactionEnded = true }, TimeSpan.Zero);
            var open = WorkspaceLogic.ExecutedStatus(block, new ExecuteResult { Kind = SqlKind.PlSql, Summary = "실행함 (커밋 전)" }, TimeSpan.Zero);

            Assert.Equal("실행함 (블록 안에서 커밋·롤백됨)  0.00초", ended.PlainText);
            Assert.EndsWith("커밋 전에는 다른 세션에 보이지 않습니다. 같은 DB의 다른 탭에는 보입니다(세션 공유).", open.PlainText);
        }

        [Fact]
        public void ConnectionStatuses_CarryActions()
        {
            var off = WorkspaceLogic.NotConnectedStatus("개발");
            Assert.Equal("대상 DB(개발)에 연결되어 있지 않습니다.", off.PlainText);
            Assert.Equal(WorkspaceLogic.StatusAction.Connect, off.Action);
            Assert.Equal("연결", off.ActionText);

            var broken = WorkspaceLogic.BrokenStatus("운영");
            Assert.Equal(WorkspaceLogic.StatusAction.Reconnect, broken.Action);
            Assert.Equal("다시 연결", broken.ActionText);
            Assert.Contains("운영", broken.PlainText);
        }

        [Fact]
        public void ReconnectQuestion_WarnsLostPending()
        {
            Assert.Equal("대상 DB(운영)의 연결이 끊겼습니다. 다시 연결할까요?", WorkspaceLogic.ReconnectQuestion("운영", null));
            Assert.Equal("대상 DB(운영)의 연결이 끊겼습니다. 다시 연결할까요?" + Environment.NewLine + "커밋하지 않은 변경(커밋 대기 2행)은 서버에서 이미 사라졌습니다.",
                WorkspaceLogic.ReconnectQuestion("운영", "커밋 대기 2행"));
        }

        [Fact]
        public void BusyStatuses_MatchPocWording()
        {
            Assert.Equal("같은 DB(개발)에서 다른 탭이 실행 중입니다. 끝난 뒤 실행하세요 (DB마다 세션 1개).",
                WorkspaceLogic.BusyStatus("개발").PlainText);
            Assert.Contains("다른 작업", WorkspaceLogic.BusyOtherStatus("개발").PlainText);
            var fetch = WorkspaceLogic.BusyFetchStatus("개발", true);
            Assert.Equal("같은 DB(개발)에서 다른 탭이 실행 중입니다. 끝난 뒤 가져오세요.", fetch.PlainText);
            // 끝난 뒤 다시 누를 수 있게 버튼을 남긴다
            Assert.Equal(WorkspaceLogic.StatusAction.FetchNext, fetch.Action);
        }

        [Fact]
        public void RunningStatus_ShowsElapsed()
        {
            Assert.Equal("실행 중… 1.2초  [취소]로 멈출 수 있습니다.", WorkspaceLogic.RunningStatus(TimeSpan.FromMilliseconds(1210), false).PlainText);
            Assert.Equal("취소하는 중… 3.0초", WorkspaceLogic.RunningStatus(TimeSpan.FromSeconds(3), true).PlainText);
            Assert.Equal("다음 200행 가져오는 중… 0.4초  열어 둔 커서에서 이어서 읽습니다(다시 조회하지 않음).",
                WorkspaceLogic.FetchingStatus(200, TimeSpan.FromMilliseconds(420), false).PlainText);
            Assert.Equal("준비", WorkspaceLogic.ReadyStatus().PlainText);
        }

        // ---------- 결과 ----------

        [Fact]
        public void TsvCell_NullEmptyAndBreaksToSpace()
        {
            Assert.Equal("", WorkspaceLogic.TsvCell(null));
            Assert.Equal("", WorkspaceLogic.TsvCell(""));
            Assert.Equal("a b c d", WorkspaceLogic.TsvCell("a\tb\r\nc\nd"));
            Assert.Equal("x y", WorkspaceLogic.TsvCell("x\ry"));
        }

        [Fact]
        public void ToTsv_HeaderAndRowsWithCrLf()
        {
            var rows = new List<string[]> { new[] { "7839", "KING", null }, new[] { "7902", "FO\tRD", "300" } };
            Assert.Equal("EMPNO\tENAME\tCOMM\r\n7839\tKING\t\r\n7902\tFO RD\t300",
                WorkspaceLogic.ToTsv(new[] { "EMPNO", "ENAME", "COMM" }, rows));
            Assert.Equal("7839\tKING\t", WorkspaceLogic.ToTsv(null, rows.Take(1)));
            Assert.Equal("A", WorkspaceLogic.ToTsv(new[] { "A" }, null));
            Assert.Equal("", WorkspaceLogic.ToTsv(null, new List<string[]>()));
        }

        [Fact]
        public void CellText_KeepsOneLine()
        {
            Assert.Equal("NULL", WorkspaceLogic.CellText(null));
            Assert.Equal("KING", WorkspaceLogic.CellText("KING"));
            Assert.Equal("a↵b↵c↵d e", WorkspaceLogic.CellText("a\r\nb\nc\rd\te"));
            // [셀 복사]는 표시 글이 아니라 원래 값: 줄바꿈·탭 유지, NULL은 빈 글
            Assert.Equal("a\r\nb\td", WorkspaceLogic.CellCopyText(new[] { "x", "a\r\nb\td" }, 1));
            Assert.Equal("", WorkspaceLogic.CellCopyText(new string[] { null }, 0));
            Assert.Equal("", WorkspaceLogic.CellCopyText(new[] { "x" }, 3));
            Assert.Equal("", WorkspaceLogic.CellCopyText(null, 0));
            Assert.True(WorkspaceLogic.HasLineBreak("a\nb"));
            Assert.False(WorkspaceLogic.HasLineBreak("a\tb"));
            Assert.False(WorkspaceLogic.HasLineBreak(null));
        }

        [Fact]
        public void TooltipText_WrapsAndCaps()
        {
            Assert.Null(WorkspaceLogic.TooltipText(null));
            Assert.Equal("short", WorkspaceLogic.TooltipText("short"));
            Assert.Equal("abcd\nefgh\nij", WorkspaceLogic.TooltipText("abcdefghij", 4, 100));
            Assert.Equal("ab\ncd\nef", WorkspaceLogic.TooltipText("ab\r\ncd\ref", 4, 100));
            Assert.Equal("abcd\nef…", WorkspaceLogic.TooltipText("abcdefghij", 4, 6));
            var emoji = WorkspaceLogic.TooltipText("abc\U0001F600de", 4, 100);
            Assert.Equal("abc\n\U0001F600de", emoji);
        }

        [Fact]
        public void ColumnWidth_ClampsAndGrowsWithContent()
        {
            Assert.Equal(WorkspaceLogic.MinColumnWidth, WorkspaceLogic.ColumnWidth("A", null, new[] { "Y" }));
            // 형식 표시도 너비에 들어간다
            Assert.True(WorkspaceLogic.ColumnWidth("A", "TIMESTAMP(6) WITH TIME ZONE", new[] { "Y" }) > WorkspaceLogic.MinColumnWidth);
            Assert.Equal(WorkspaceLogic.MaxColumnWidth, WorkspaceLogic.ColumnWidth("TEXT", "CLOB", new[] { new string('x', 4000) }));
            var narrow = WorkspaceLogic.ColumnWidth("ENAME", "VARCHAR2(10)", new[] { "KING" });
            var wide = WorkspaceLogic.ColumnWidth("ENAME", "VARCHAR2(10)", new[] { "KING", "가나다라마바사아자차" });
            Assert.True(wide > narrow);
            Assert.True(WorkspaceLogic.ColumnWidth("X", "NUMBER", new string[] { null }) >= WorkspaceLogic.MinColumnWidth);
            Assert.True(WorkspaceLogic.RowNumberWidth(100000) > WorkspaceLogic.RowNumberWidth(9));
        }

        // ---------- 결과 정렬 ----------

        private static string[] SortValues(IEnumerable<string> values, bool numeric, bool descending)
        {
            var rows = values.Select((v, i) => new { Values = new[] { v }, Number = i + 1 }).ToList();
            rows.Sort((a, b) => WorkspaceLogic.CompareRows(a.Values, a.Number, b.Values, b.Number, 0, numeric, descending));
            return rows.Select(r => r.Values[0]).ToArray();
        }

        [Fact]
        public void CompareRows_NumericColumn_SortsByValueNotText()
        {
            var values = new[] { "10", "9", "-1.5", "100", "0.25", "1E+40", null, "2" };

            Assert.Equal(new[] { "-1.5", "0.25", "2", "9", "10", "100", "1E+40", null }, SortValues(values, true, false));
            // 내림차순은 NULL이 맨 앞(Oracle DESC NULLS FIRST)
            Assert.Equal(new[] { null, "1E+40", "100", "10", "9", "2", "0.25", "-1.5" }, SortValues(values, true, true));
        }

        [Fact]
        public void CompareRows_NumericColumn_BinaryDoubleSpecials()
        {
            var values = new[] { "Infinity", "0.125", "-Infinity", "NaN", "3" };

            Assert.Equal(new[] { "NaN", "-Infinity", "0.125", "3", "Infinity" }, SortValues(values, true, false));
        }

        [Fact]
        public void CompareRows_NumericColumn_UnparsableTextGoesAfterNumbers()
        {
            Assert.Equal(new[] { "1", "2", "(OBJECT)" }, SortValues(new[] { "(OBJECT)", "2", "1" }, true, false));
        }

        [Fact]
        public void CompareRows_TextColumn_OrdinalLikeOracleBinary()
        {
            var values = new[] { "나", "b", "가", "B", null, "2026-01-02 00:00:00", "2025-12-31 23:59:59" };

            Assert.Equal(new[] { "2025-12-31 23:59:59", "2026-01-02 00:00:00", "B", "b", "가", "나", null }, SortValues(values, false, false));
        }

        [Fact]
        public void CompareRows_Ties_KeepOriginalOrder()
        {
            var rows = new[] { "A", "B", "A", "B", "A" }.Select((v, i) => new { Values = new[] { v }, Number = i + 1 }).ToList();

            rows.Sort((a, b) => WorkspaceLogic.CompareRows(a.Values, a.Number, b.Values, b.Number, 0, false, false));
            Assert.Equal(new[] { 1, 3, 5, 2, 4 }, rows.Select(r => r.Number).ToArray());

            rows.Sort((a, b) => WorkspaceLogic.CompareRows(a.Values, a.Number, b.Values, b.Number, 0, false, true));
            Assert.Equal(new[] { 2, 4, 1, 3, 5 }, rows.Select(r => r.Number).ToArray());
        }

        [Fact]
        public void CompareRows_MissingColumnIsNull()
        {
            Assert.True(WorkspaceLogic.CompareRows(new[] { "x" }, 1, new string[0], 2, 0, false, false) < 0);
        }

        [Theory]
        [InlineData(-1, false, 2, 2, false)]  // 정렬 없음 → 누른 열 오름차순
        [InlineData(2, false, 2, 2, true)]    // 오름차순 → 내림차순
        [InlineData(2, true, 2, -1, false)]   // 내림차순 → 해제
        [InlineData(2, true, 0, 0, false)]    // 다른 열 → 그 열 오름차순
        public void NextSort_CyclesAscendingDescendingOff(int current, bool currentDesc, int clicked, int expectedColumn, bool expectedDesc)
        {
            int column;
            bool descending;
            WorkspaceLogic.NextSort(current, currentDesc, clicked, out column, out descending);

            Assert.Equal(expectedColumn, column);
            Assert.Equal(expectedDesc, descending);
        }

        [Fact]
        public void SortMessages()
        {
            Assert.Equal(" ▲", WorkspaceLogic.SortMark(false));
            Assert.Equal(" ▼", WorkspaceLogic.SortMark(true));
            Assert.StartsWith("가져온 1,000행 안에서만 정렬했습니다.", WorkspaceLogic.SortPartialMessage(1000));
        }
    }
}

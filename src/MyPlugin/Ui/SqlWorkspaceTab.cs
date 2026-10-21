using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Windows.Controls;

namespace MyPlugin
{
    /// <summary>결과 영역에서 보이는 칸.</summary>
    internal enum WorkspacePane { Grid, Messages, History }

    /// <summary>
    /// SQL 탭 하나의 상태. 편집기(SqlEditor)는 탭마다 따로 두어 텍스트·캐럿·선택·스크롤·되돌리기 기록이 탭별로 유지된다.
    /// UI 스레드에서만 읽고 쓴다.
    /// </summary>
    internal sealed class SqlTabState
    {
        private readonly string _baseTitle;

        public SqlTabState(string title, string dbId, SqlEditor editor)
        {
            _baseTitle = title;
            DbId = dbId;
            Editor = editor;
        }

        /// <summary>탭 이름: 파일 탭이면 파일 이름, 아니면 "SQL n".</summary>
        public string Title
        {
            get { return FilePath != null ? System.IO.Path.GetFileName(FilePath) : _baseTitle; }
        }

        // ---- 파일 ----

        /// <summary>열었거나 저장한 SQL 파일. 없으면 null(저장한 적 없는 탭).</summary>
        public string FilePath { get; set; }

        /// <summary>파일 인코딩(저장할 때 그대로 쓴다). 새 파일은 BOM 없는 UTF-8.</summary>
        public SqlFileEncoding FileEncoding { get; set; } = SqlFileEncoding.Utf8;

        /// <summary>파일의 줄바꿈("\r\n"·"\n"). 편집기는 늘 Environment.NewLine이므로 저장할 때 되돌린다. 모르면 null.</summary>
        public string FileNewline { get; set; }

        /// <summary>파일에서 읽었거나 마지막으로 저장한 때의 편집기 글. null이면 아직 저장하지 않은 내용(파일 탭이면 변경됨).</summary>
        public string SavedText { get; set; }

        /// <summary>파일 탭인데 저장한 뒤 바뀌었음.</summary>
        public bool Dirty
        {
            get { return FilePath != null && !string.Equals(Editor.Text, SavedText, StringComparison.Ordinal); }
        }

        /// <summary>탭 머리에 지금 보이는 변경 표시(바뀔 때만 다시 그린다).</summary>
        public bool ShownDirty { get; set; }

        /// <summary>대상 DB(접속 Id). 없으면 null.</summary>
        public string DbId { get; set; }

        public SqlEditor Editor { get; }

        /// <summary>탭을 만들 때의 글(첫 탭 안내문·새 탭 머리말). 이것과 같으면 임시 저장하지 않는다. 되살린 탭은 "".</summary>
        public string InitialText { get; set; } = "";

        // ---- 조회 결과 ----
        // 문장 하나를 실행하면 결과 하나, 여러 문장(선택 영역·스크립트)을 실행하면 조회마다 결과 하나(결과 하위 탭).
        // 아래 속성들은 지금 고른 결과(ActiveResult)를 가리킨다 — 결과 하나일 때와 같은 코드로 그리드·상태줄·다음 행을 다룬다.

        /// <summary>이 탭의 조회 결과들(실행 순서). 조회한 적 없으면 비어 있다.</summary>
        public List<ResultSetState> Results { get; } = new List<ResultSetState>();

        /// <summary>지금 보이는 결과. 없으면 null.</summary>
        public ResultSetState ActiveResult { get; set; }

        /// <summary>마지막 조회의 열. 조회한 적 없으면 null.</summary>
        public List<ResultColumn> Columns
        {
            get { return ActiveResult != null ? ActiveResult.Columns : null; }
        }

        public ObservableCollection<ResultGridRow> Rows
        {
            get { return ActiveResult != null ? ActiveResult.Rows : null; }
        }

        /// <summary>지금 결과의 그리드(처음 보일 때 만든다).</summary>
        public ResultGridView Grid
        {
            get { return ActiveResult != null ? ActiveResult.Grid : null; }
            set
            {
                if (ActiveResult != null)
                    ActiveResult.Grid = value;
            }
        }

        public QueryCursor Cursor
        {
            get { return ActiveResult != null ? ActiveResult.Cursor : null; }
        }

        public DbSession CursorSession
        {
            get { return ActiveResult != null ? ActiveResult.CursorSession : null; }
        }

        /// <summary>결과(커서)를 만든 DB. 대상을 바꿔도 결과는 이 DB의 것이다.</summary>
        public string ResultDbId
        {
            get { return ActiveResult != null ? ActiveResult.ResultDbId : null; }
        }

        public bool CursorHadMore
        {
            get { return ActiveResult != null && ActiveResult.CursorHadMore; }
        }

        public TimeSpan? LastElapsed
        {
            get { return ActiveResult != null ? ActiveResult.LastElapsed : null; }
            set
            {
                if (ActiveResult != null)
                    ActiveResult.LastElapsed = value;
            }
        }

        public bool StrippedSemicolon
        {
            get { return ActiveResult != null && ActiveResult.StrippedSemicolon; }
        }

        public bool SortNoticeShown
        {
            get { return ActiveResult != null && ActiveResult.SortNoticeShown; }
            set
            {
                if (ActiveResult != null)
                    ActiveResult.SortNoticeShown = value;
            }
        }

        /// <summary>지금 결과의 열린 커서에서 더 가져올 수 있음.</summary>
        public bool HasMoreRows
        {
            get { return ActiveResult != null && ActiveResult.HasMoreRows; }
        }

        /// <summary>이 탭의 결과 중 아직 열린 커서(닫아야 할 서버 자원)를 쥔 것이 있음.</summary>
        public bool HoldsOpenCursor
        {
            get { return Results.Any(r => r.HoldsOpenCursor); }
        }

        public bool CursorClosedEarly
        {
            get { return ActiveResult != null && ActiveResult.CursorClosedEarly; }
        }

        // ---- 실행 ----

        /// <summary>실행 또는 다음 행 가져오기 중.</summary>
        public bool Running { get; set; }

        /// <summary>여러 문장 실행 중: 모두 몇 개(아니면 0)와 지금 몇 번째.</summary>
        public int ScriptTotal { get; set; }

        public int ScriptIndex { get; set; }

        public bool Fetching { get; set; }

        public int FetchingCount { get; set; }

        public bool CancelRequested { get; set; }

        public DbSession RunningSession { get; set; }

        public string RunningDbId { get; set; }

        public Stopwatch RunWatch { get; set; }

        /// <summary>
        /// 이 실행의 취소. DbSession은 명령을 등록하기 직전에도 토큰을 보므로, 이전 커서를 닫는 사이 누른 [취소]도 놓치지 않는다.
        /// 타이머·대기 핸들을 쓰지 않으므로 Dispose하지 않는다(취소 작업 스레드와 겹쳐 Dispose하면 안전하지 않음).
        /// </summary>
        public CancellationTokenSource RunCancel { get; set; }

        /// <summary>상태줄 [연결]·[다시 연결]을 눌러 연결하는 중.</summary>
        public bool Connecting { get; set; }

        // ---- 표시 ----

        /// <summary>다른 탭을 보는 동안 실행이 끝났음(탭 머리의 ●).</summary>
        public bool Done { get; set; }

        /// <summary>뒤에서 끝난 실행이 보여 줄 칸. 이 탭으로 옮길 때 적용한다.</summary>
        public WorkspacePane? PendingPane { get; set; }

        public WorkspaceLogic.StatusInfo Status { get; set; }

        /// <summary>새 조회 결과 하나로 바꾼다(문장 하나 실행). 이전 결과들은 버린다(그리드는 부른 쪽이 화면에서 뗀다).</summary>
        public ResultSetState SetResult(DbSession session, string dbId, ExecuteResult result, SqlStatement statement)
        {
            Results.Clear();
            return AddResult(session, dbId, result, statement);
        }

        /// <summary>조회 결과를 하나 더한다(여러 문장 실행). 처음 더한 결과가 보인다.</summary>
        public ResultSetState AddResult(DbSession session, string dbId, ExecuteResult result, SqlStatement statement)
        {
            var set = new ResultSetState(session, dbId, result, statement, Results.Count + 1);
            Results.Add(set);
            if (ActiveResult == null || !Results.Contains(ActiveResult))
                ActiveResult = set;
            return set;
        }

        /// <summary>결과를 모두 버린다(여러 문장 실행 시작). 그리드는 부른 쪽이 화면에서 뗀다.</summary>
        public void ClearResults()
        {
            Results.Clear();
            ActiveResult = null;
        }

        /// <summary>가져온 행을 지금 결과 끝에 붙이고 처음 붙인 행을 돌려준다(없으면 null).</summary>
        public ResultGridRow AppendRows(List<string[]> rows)
        {
            return ActiveResult != null ? ActiveResult.AppendRows(rows) : null;
        }
    }

    /// <summary>조회 결과 하나(열·행·열린 커서·그리드). 여러 문장을 실행하면 한 탭에 여럿이 생긴다.</summary>
    internal sealed class ResultSetState
    {
        public ResultSetState(DbSession session, string dbId, ExecuteResult result, SqlStatement statement, int number)
        {
            Number = number;
            Sql = statement != null ? statement.Text : null;
            StrippedSemicolon = statement != null && statement.StrippedSemicolon;
            Cursor = result.Cursor;
            CursorSession = session;
            ResultDbId = dbId;
            CursorHadMore = result.Cursor != null && result.Cursor.HasMore;
            Columns = result.Cursor != null ? new List<ResultColumn>(result.Cursor.Columns) : new List<ResultColumn>();
            var rows = new List<ResultGridRow>(result.Rows != null ? result.Rows.Count : 0);
            if (result.Rows != null)
            {
                foreach (var values in result.Rows)
                    rows.Add(new ResultGridRow(rows.Count + 1, values));
            }
            Rows = new ObservableCollection<ResultGridRow>(rows);
        }

        /// <summary>실행 안에서 몇 번째 조회 결과인지(1부터).</summary>
        public int Number { get; }

        /// <summary>결과를 만든 문장.</summary>
        public string Sql { get; }

        public List<ResultColumn> Columns { get; }

        public ObservableCollection<ResultGridRow> Rows { get; }

        /// <summary>이 결과의 그리드(처음 보일 때 만든다).</summary>
        public ResultGridView Grid { get; set; }

        public QueryCursor Cursor { get; }

        public DbSession CursorSession { get; }

        public string ResultDbId { get; }

        /// <summary>이 결과가 커서를 놓았음(대상 변경·탭 닫기·끊기·새 실행). 실제로 닫혔는지와 무관하게 더 가져오지 않는다.</summary>
        public bool CursorReleased { get; set; }

        /// <summary>마지막 실행·가져오기 직후 커서에 행이 더 있었음. 지금 닫혔으면 "끝까지 읽음"이 아니라 중간에 닫힌 것이다.</summary>
        public bool CursorHadMore { get; set; }

        public TimeSpan? LastElapsed { get; set; }

        public bool StrippedSemicolon { get; }

        /// <summary>이 결과에서 "가져온 행 안에서만 정렬" 안내를 이미 했음.</summary>
        public bool SortNoticeShown { get; set; }

        /// <summary>빠른 조회(F1) 결과의 편집 대상. 편집할 수 없는 결과(일반 실행)는 null.</summary>
        public EditableTable Edit { get; set; }

        public int ModifiedCount
        {
            get { return Rows.Count(r => r.State == RowEditState.Modified); }
        }

        public int DeletedCount
        {
            get { return Rows.Count(r => r.State == RowEditState.Deleted); }
        }

        public int InsertedCount
        {
            get { return Rows.Count(r => r.State == RowEditState.Inserted); }
        }

        /// <summary>DB에 보내지 않은 추가·수정·삭제 표시가 있음.</summary>
        public bool HasPendingEdits
        {
            get { return Rows.Any(r => r.State != RowEditState.Unchanged); }
        }

        public bool HasMoreRows
        {
            get { return Cursor != null && !CursorReleased && !Cursor.IsClosed && Cursor.HasMore; }
        }

        public bool HoldsOpenCursor
        {
            get { return Cursor != null && !CursorReleased && !Cursor.IsClosed; }
        }

        public bool CursorClosedEarly
        {
            get { return Cursor != null && CursorHadMore && !HasMoreRows; }
        }

        /// <summary>가져온 행을 끝에 붙이고 처음 붙인 행을 돌려준다(없으면 null).</summary>
        public ResultGridRow AppendRows(List<string[]> rows)
        {
            ResultGridRow first = null;
            if (rows == null)
                return null;
            foreach (var values in rows)
            {
                var row = new ResultGridRow(Rows.Count + 1, values);
                // ROWID 키: 더 가져온 행도 끝의 ROWID를 뗀다(그리드에 붙이기 전에)
                if (Edit != null && Edit.UsesRowId)
                    row.TakeRowId();
                Rows.Add(row);
                if (first == null)
                    first = row;
            }
            CursorHadMore = Cursor != null && !Cursor.IsClosed && Cursor.HasMore;
            return first;
        }
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;

namespace MyPlugin
{
    /// <summary>
    /// 빠른 조회(F1) 결과 편집: 그리드 위 편집 막대([행 추가]·[복제]·[편집]·[삭제] │ [저장] │ WHERE 칸·[다시 조회])와 저장(DELETE·UPDATE·INSERT 실행).
    /// 고친 행·삭제 표시한 행은 그리드가 바탕색·표시(✎·✖)로 드러내고, [저장]은 기본 키로 행마다 문장 하나를 실행한다(커밋 전 — [커밋]·[롤백]).
    /// </summary>
    internal sealed partial class SqlWorkspace
    {
        private Border _editBar;
        private Button _addRow;
        private Button _duplicateRow;
        private Button _editRow;
        private Button _deleteRows;
        private Button _saveEdits;
        private TextBlock _editInfo;
        private TextBox _editWhere;
        private Button _requery;
        // WHERE 칸을 마지막으로 채운 결과: 같은 결과를 다시 그릴 때 입력 중인 조건을 덮어쓰지 않게
        private ResultSetState _editWhereResult;

        private FrameworkElement BuildEditBar()
        {
            var bar = new DockPanel { Margin = new Thickness(4, 2, 4, 2) };
            // 순서: 행을 바꾸는 버튼(추가 → 복제 → 편집 → 삭제) │ DB에 보내는 [저장] │ 다시 조회
            _addRow = EditBarButton("행 추가", "빈 새 행을 편집 창에서 채워 추가합니다. [저장]하면 INSERT");
            _duplicateRow = EditBarButton("복제", "고른 행의 값으로 새 행을 만듭니다(편집 창에서 기본 키를 바꾸세요). [저장]하면 INSERT");
            _editRow = EditBarButton("편집", "고른 행의 값을 고칩니다(편집 창)");
            _deleteRows = EditBarButton("삭제", "고른 행에 삭제 표시를 합니다(다시 누르면 풂, 저장 전 새 행은 바로 뺌). [저장]하면 DELETE");
            _saveEdits = EditBarButton("저장", "삭제 표시한 행은 DELETE, 고친 행은 UPDATE, 새 행은 INSERT로 DB에 보냅니다(커밋 전)");
            // 행 버튼 묶음과 [저장]을 떼어 잘못 누르지 않게
            _saveEdits.Margin = new Thickness(12, 0, 4, 0);
            _addRow.Click += (s, e) => Safe(AddRow);
            _duplicateRow.Click += (s, e) => Safe(DuplicateSelectedRow);
            _editRow.Click += (s, e) => Safe(EditSelectedRow);
            _deleteRows.Click += (s, e) => Safe(ToggleDeleteSelectedRows);
            _saveEdits.Click += async (s, e) =>
            {
                try
                {
                    await SaveEditsAsync();
                }
                catch (Exception ex)
                {
                    ReportUnexpected(ex);
                }
            };
            bar.Children.Add(_addRow);
            bar.Children.Add(_duplicateRow);
            bar.Children.Add(_editRow);
            bar.Children.Add(_deleteRows);
            bar.Children.Add(_saveEdits);
            var whereLabel = Theme.Text("WHERE", Theme.SecondaryText);
            whereLabel.FontSize = 11.5;
            whereLabel.VerticalAlignment = VerticalAlignment.Center;
            whereLabel.Margin = new Thickness(12, 0, 4, 0);
            DockPanel.SetDock(whereLabel, Dock.Left);
            bar.Children.Add(whereLabel);
            _editWhere = new TextBox
            {
                Width = 280,
                FontFamily = Theme.Mono,
                Margin = new Thickness(0, 0, 4, 0),
                VerticalContentAlignment = VerticalAlignment.Center,
                ToolTip = "조건을 넣고 Enter·[다시 조회] — 예: ORDER_ID = 1001, STATUS IN ('A','B'). 비우면 조건 없이 조회.\n';'·주석·짝 안 맞는 괄호는 쓸 수 없습니다(조건은 WHERE ( … ) 괄호 안에서만 실행)."
            };
            SpellCheck.SetIsEnabled(_editWhere, false);
            AutomationProperties.SetName(_editWhere, "WHERE 조건");
            DockPanel.SetDock(_editWhere, Dock.Left);
            _editWhere.KeyDown += (s, e) =>
            {
                if (e.Key == Key.Enter)
                {
                    e.Handled = true;
                    Safe(RequeryWithWhere);
                }
            };
            bar.Children.Add(_editWhere);
            _requery = EditBarButton("다시 조회", "WHERE 조건으로 이 테이블을 다시 조회합니다(앞 " + WorkspaceLogic.QuickQueryRows.ToString(System.Globalization.CultureInfo.InvariantCulture) + "행)");
            _requery.Click += (s, e) => Safe(RequeryWithWhere);
            bar.Children.Add(_requery);
            _editInfo = Theme.Text("", Theme.SecondaryText);
            _editInfo.FontSize = 11.5;
            _editInfo.VerticalAlignment = VerticalAlignment.Center;
            _editInfo.Margin = new Thickness(8, 0, 4, 0);
            _editInfo.TextTrimming = TextTrimming.CharacterEllipsis;
            bar.Children.Add(_editInfo);

            _editBar = new Border { Child = bar, BorderThickness = new Thickness(0, 0, 0, 1), Visibility = Visibility.Collapsed };
            _editBar.SetResourceReference(Border.BorderBrushProperty, Theme.Border);
            Theme.Background(_editBar, Theme.SurfaceBackground);
            AutomationProperties.SetName(_editBar, "결과 편집");
            DockPanel.SetDock(_editBar, Dock.Top);
            return _editBar;
        }

        private static Button EditBarButton(string text, string tip)
        {
            var button = new Button
            {
                Content = text,
                Padding = new Thickness(10, 1, 10, 1),
                Margin = new Thickness(0, 0, 4, 0),
                IsEnabled = false,
                ToolTip = tip
            };
            DockPanel.SetDock(button, Dock.Left);
            return button;
        }

        /// <summary>편집 막대: 지금 결과가 빠른 조회 결과일 때만 보인다. 버튼은 편집할 수 있고 실행 중이 아닐 때만 켠다.</summary>
        private void RenderEditBar()
        {
            if (_editBar == null)
                return;
            var tab = _active;
            var result = tab != null ? tab.ActiveResult : null;
            var edit = result != null ? result.Edit : null;
            if (edit == null)
            {
                _editBar.Visibility = Visibility.Collapsed;
                return;
            }
            _editBar.Visibility = Visibility.Visible;
            if (!ReferenceEquals(_editWhereResult, result))
            {
                _editWhereResult = result;
                _editWhere.Text = edit.Where ?? "";
            }
            var profile = Profile(result.ResultDbId);
            _editWhere.IsEnabled = !tab.Running;
            _requery.IsEnabled = !tab.Running && profile != null;
            var session = _host.GetSession(result.ResultDbId);
            string blocked = null;
            if (!edit.CanEdit)
                blocked = edit.DisabledReason;
            else if (profile == null)
                blocked = "접속이 삭제되어 편집할 수 없습니다.";
            else if (profile.ReadOnly)
                blocked = "읽기 전용 접속은 편집할 수 없습니다.";
            else if (session == null || session.IsBroken)
                blocked = "연결이 끊겨 저장할 수 없습니다.";
            var usable = blocked == null && !tab.Running;
            _addRow.IsEnabled = usable;
            _duplicateRow.IsEnabled = usable;
            _editRow.IsEnabled = usable;
            _deleteRows.IsEnabled = usable;
            _saveEdits.IsEnabled = usable && result.HasPendingEdits;
            var target = WorkspaceLogic.QualifiedName(edit.Owner, edit.Name);
            if (blocked != null)
            {
                _editInfo.Text = target + " — " + blocked;
                return;
            }
            var pending = RowEditLogic.PendingSummary(result.ModifiedCount, result.DeletedCount, result.InsertedCount);
            _editInfo.Text = pending.Length == 0
                ? target + " — 바꾼 행은 [저장]으로 DB에 보냅니다(커밋 전)"
                : target + " — " + pending + " (저장하지 않음)";
        }

        /// <summary>
        /// WHERE 칸 Enter·[다시 조회]: 조건을 검사하고(<see cref="WorkspaceLogic.QuickQueryWhere"/>) 같은 테이블을 빠른 조회로 다시 조회한다.
        /// 저장하지 않은 수정·삭제 표시가 있으면 버릴지 먼저 묻는다.
        /// </summary>
        private void RequeryWithWhere()
        {
            var tab = _active;
            var result = tab != null ? tab.ActiveResult : null;
            var edit = result != null ? result.Edit : null;
            if (edit == null || tab.Running)
                return;
            string error;
            var where = WorkspaceLogic.QuickQueryWhere(_editWhere.Text, out error);
            if (where == null)
            {
                SetStatus(tab, WorkspaceLogic.TextStatus("WHERE 조건을 쓸 수 없습니다: " + error));
                _editWhere.Focus();
                return;
            }
            var profile = Profile(result.ResultDbId);
            if (profile == null)
                return;
            if (result.HasPendingEdits && !Dialogs.ConfirmDiscardEdits(Window.GetWindow(this), profile,
                    WorkspaceLogic.QualifiedName(edit.Owner, edit.Name), RowEditLogic.PendingSummary(result.ModifiedCount, result.DeletedCount, result.InsertedCount)))
                return;
            QuickQuery(result.ResultDbId, edit.Owner, edit.Name, where);
        }

        private const string NoInsertValues = "넣을 값이 하나도 없어 새 행을 만들지 않았습니다(빈 칸·NULL 열은 INSERT에서 빠짐).";

        /// <summary>[행 추가]: 빈 새 행을 편집 창에서 채운다. 취소하거나 값이 하나도 없으면 만들지 않는다.</summary>
        private void AddRow()
        {
            var tab = _active;
            var result = tab != null ? tab.ActiveResult : null;
            if (result == null || result.Edit == null || !result.Edit.CanEdit || tab.Running || result.Grid == null)
                return;
            AddInsertedRow(tab, result, new string[result.Edit.Columns.Count], "행 추가");
        }

        /// <summary>[복제]: 고른 행(여럿이면 번호가 가장 작은 행)의 값으로 새 행을 만든다. 기본 키가 겹치지 않게 편집 창을 먼저 연다.</summary>
        private void DuplicateSelectedRow()
        {
            var tab = _active;
            var result = tab != null ? tab.ActiveResult : null;
            if (result == null || result.Edit == null || !result.Edit.CanEdit || tab.Running || result.Grid == null)
                return;
            var source = result.Grid.SelectedRows().FirstOrDefault();
            if (source == null)
            {
                SetStatus(tab, WorkspaceLogic.TextStatus("복제할 행을 고르세요."));
                return;
            }
            AddInsertedRow(tab, result, RowEditLogic.CopyForInsert(result.Edit, source.Values), "행 복제");
        }

        private void AddInsertedRow(SqlTabState tab, ResultSetState result, string[] initial, string title)
        {
            var values = RowEditDialog.Show(Window.GetWindow(this), Profile(result.ResultDbId), result.Edit, initial, true, title);
            if (values == null)
                return;
            // 편집 창을 띄운 사이 결과가 바뀌었을 수 있다
            if (tab.Running || tab.ActiveResult != result || result.Grid == null)
                return;
            var blocked = RowEditLogic.InsertBlockReason(result.Edit, values);
            if (blocked != null)
            {
                SetStatus(tab, WorkspaceLogic.TextStatus(blocked));
                return;
            }
            if (RowEditLogic.InsertStatement(result.Edit, values) == null)
            {
                SetStatus(tab, WorkspaceLogic.TextStatus(NoInsertValues));
                return;
            }
            var number = result.Rows.Count == 0 ? 1 : result.Rows.Max(r => r.Number) + 1;
            var row = ResultGridRow.NewInserted(number, values);
            result.Rows.Add(row);
            result.Grid.FitRowNumbers(number);
            result.Grid.View.SelectedItem = row;
            result.Grid.ScrollIntoView(row);
            RenderEditBar();
            SetStatus(tab, WorkspaceLogic.TextStatus("새 행을 추가했습니다 — [저장]하면 INSERT합니다(커밋 전)."));
        }

        /// <summary>[편집]: 고른 행(여럿이면 첫 행)의 값을 편집 창에서 고친다. 삭제 표시한 행은 먼저 표시를 풀어야 한다.</summary>
        private void EditSelectedRow()
        {
            var tab = _active;
            var result = tab != null ? tab.ActiveResult : null;
            if (result == null || result.Edit == null || !result.Edit.CanEdit || tab.Running || result.Grid == null)
                return;
            var row = result.Grid.SelectedRows().FirstOrDefault();
            if (row == null)
            {
                SetStatus(tab, WorkspaceLogic.TextStatus("편집할 행을 고르세요."));
                return;
            }
            if (row.State == RowEditState.Deleted)
            {
                SetStatus(tab, WorkspaceLogic.TextStatus("삭제 표시한 행은 고칠 수 없습니다. [삭제]를 다시 눌러 표시를 푸세요."));
                return;
            }
            var inserted = row.State == RowEditState.Inserted;
            var values = RowEditDialog.Show(Window.GetWindow(this), Profile(result.ResultDbId), result.Edit, row.Values, inserted, inserted ? "새 행 편집" : "행 편집",
                inserted ? null : row.OriginalValues);
            if (values == null)
                return;
            var blocked = inserted ? RowEditLogic.InsertBlockReason(result.Edit, values) : RowEditLogic.UpdateBlockReason(result.Edit, row.OriginalValues, values);
            if (blocked != null)
            {
                SetStatus(tab, WorkspaceLogic.TextStatus(blocked));
                return;
            }
            if (inserted && RowEditLogic.InsertStatement(result.Edit, values) == null)
            {
                SetStatus(tab, WorkspaceLogic.TextStatus(NoInsertValues));
                return;
            }
            row.Apply(values);
            RenderEditBar();
            SetStatus(tab, WorkspaceLogic.TextStatus(inserted ? "새 행을 고쳤습니다 — [저장]하면 INSERT합니다(커밋 전)."
                : row.State == RowEditState.Modified
                ? row.Number.ToString(System.Globalization.CultureInfo.InvariantCulture) + "행을 고쳤습니다 — [저장]으로 DB에 보냅니다(커밋 전)."
                : "원래 값과 같아 변경이 없습니다."));
        }

        /// <summary>[삭제]: 고른 행에 삭제 표시를 한다. 고른 행이 모두 삭제 표시면 푼다.</summary>
        private void ToggleDeleteSelectedRows()
        {
            var tab = _active;
            var result = tab != null ? tab.ActiveResult : null;
            if (result == null || result.Edit == null || !result.Edit.CanEdit || tab.Running || result.Grid == null)
                return;
            var rows = result.Grid.SelectedRows();
            if (rows.Count == 0)
            {
                SetStatus(tab, WorkspaceLogic.TextStatus("삭제 표시할 행을 고르세요."));
                return;
            }
            // 저장 전 새 행은 DB에 없으니 표시하지 않고 바로 뺀다
            var added = rows.Where(r => r.State == RowEditState.Inserted).ToList();
            foreach (var row in added)
                result.Rows.Remove(row);
            rows = rows.Except(added).ToList();
            if (rows.Count == 0)
            {
                RenderEditBar();
                SetStatus(tab, WorkspaceLogic.TextStatus(added.Count.ToString(System.Globalization.CultureInfo.InvariantCulture) + "개 새 행을 뺐습니다(저장 전이라 DB와 무관)."));
                return;
            }
            var unmark = rows.All(r => r.State == RowEditState.Deleted);
            foreach (var row in rows)
                row.MarkDeleted(!unmark);
            RenderEditBar();
            var count = rows.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
            SetStatus(tab, WorkspaceLogic.TextStatus(unmark
                ? count + "행의 삭제 표시를 풀었습니다."
                : count + "행에 삭제 표시를 했습니다 — [저장]하면 DELETE를 실행합니다(커밋 전)."));
        }

        /// <summary>
        /// [저장]: 고친 행마다 UPDATE(바뀐 열만), 삭제 표시한 행마다 DELETE를 기본 키로 실행한다. 확인 창에 문장을 보인 뒤 차례로 실행하고,
        /// 오류나 영향 행 수가 1이 아니면 그 행에서 멈춘다(이미 실행한 문장은 커밋 대기 — [롤백]으로 되돌릴 수 있다).
        /// </summary>
        private async Task SaveEditsAsync()
        {
            var tab = _active;
            var result = tab != null ? tab.ActiveResult : null;
            if (result == null || result.Edit == null || !result.Edit.CanEdit || tab.Running)
                return;
            var dbId = result.ResultDbId;
            var profile = Profile(dbId);
            if (profile == null)
                return;
            if (profile.ReadOnly)
            {
                SetStatus(tab, WorkspaceLogic.ReadOnlyStatus());
                return;
            }
            // DELETE → UPDATE → INSERT: 지운 행과 같은 키로 새 행을 넣어도 겹치지 않게
            var changes = new List<KeyValuePair<ResultGridRow, SqlQuery>>();
            foreach (var row in result.Rows.Where(r => r.State != RowEditState.Unchanged).OrderBy(r => SaveOrder(r.State)).ThenBy(r => r.Number))
            {
                // 편집 창과 같은 기준으로 한 번 더: 고칠 수 없는 칸(LOB·잘린 값)이 바뀌었으면 아무것도 보내지 않는다
                var blocked = row.State == RowEditState.Inserted ? RowEditLogic.InsertBlockReason(result.Edit, row.Values)
                    : row.State == RowEditState.Modified ? RowEditLogic.UpdateBlockReason(result.Edit, row.OriginalValues, row.Values)
                    : null;
                if (blocked != null)
                {
                    var text = row.Number.ToString(System.Globalization.CultureInfo.InvariantCulture) + "행: " + blocked;
                    AddMessage(dbId, "결과 편집을 저장하지 않았습니다. " + text, MessageKind.Error);
                    SetStatus(tab, WorkspaceLogic.TextStatus(text));
                    return;
                }
                var query = row.State == RowEditState.Deleted ? RowEditLogic.DeleteStatement(result.Edit, row.OriginalValues)
                    : row.State == RowEditState.Inserted ? RowEditLogic.InsertStatement(result.Edit, row.Values)
                    : RowEditLogic.UpdateStatement(result.Edit, row.OriginalValues, row.Values);
                if (query != null)
                    changes.Add(new KeyValuePair<ResultGridRow, SqlQuery>(row, query));
            }
            if (changes.Count == 0)
            {
                SetStatus(tab, WorkspaceLogic.TextStatus("저장할 변경이 없습니다."));
                RenderEditBar();
                return;
            }
            var updates = changes.Count(c => c.Key.State == RowEditState.Modified);
            var inserts = changes.Count(c => c.Key.State == RowEditState.Inserted);
            var deletes = changes.Count - updates - inserts;
            var owner = Window.GetWindow(this);
            if (!Dialogs.ConfirmRowEdits(owner, profile, WorkspaceLogic.QualifiedName(result.Edit.Owner, result.Edit.Name),
                    string.Join(Environment.NewLine, changes.Select(c => RowEditLogic.Describe(c.Value))), updates, deletes, inserts))
            {
                AddMessage(dbId, "결과 편집 저장을 취소했습니다.", MessageKind.Info);
                return;
            }
            // 확인 창을 띄운 사이 다른 실행이 시작됐거나 결과가 바뀌었을 수 있다
            if (tab.Running || tab.ActiveResult != result)
                return;
            var session = CheckSession(tab, profile);
            if (session == null)
                return;

            BeginRun(tab, session, dbId, false, 0);
            var watch = tab.RunWatch;
            var token = tab.RunCancel.Token;
            var updated = 0;
            var deleted = 0;
            var inserted = 0;
            var stopped = false;
            try
            {
                foreach (var change in changes)
                {
                    if (token.IsCancellationRequested)
                    {
                        AddMessage(dbId, "결과 편집 저장을 취소했습니다. 이미 보낸 변경은 커밋 대기입니다.", MessageKind.Error);
                        stopped = true;
                        break;
                    }
                    var row = change.Key;
                    var query = change.Value;
                    var started = watch.Elapsed;
                    int affected;
                    try
                    {
                        affected = await session.ExecuteDmlAsync(query, token);
                    }
                    catch (Exception ex) when (!(ex is SessionBusyException))
                    {
                        AddMessage(dbId, (DbSession.IsCancellation(ex) ? "취소됨: " : WorkspaceLogic.ErrorMessage(DbSession.DescribeError(ex), null)) + Environment.NewLine + RowEditLogic.Describe(query), MessageKind.Error);
                        stopped = true;
                        break;
                    }
                    var elapsed = watch.Elapsed - started;
                    AddHistory(dbId, RowEditLogic.Describe(query), elapsed);
                    if (affected != 1)
                    {
                        AddMessage(dbId, RowEditLogic.AffectedMismatch(affected) + Environment.NewLine + RowEditLogic.Describe(query), MessageKind.Error);
                        stopped = true;
                        break;
                    }
                    AddMessage(dbId, WorkspaceLogic.ExecutedMessage("1행 변경됨 (커밋 전)", elapsed, RowEditLogic.Describe(query)), MessageKind.Success);
                    if (row.State == RowEditState.Deleted)
                    {
                        result.Rows.Remove(row);
                        deleted++;
                    }
                    else
                    {
                        if (row.State == RowEditState.Inserted)
                            inserted++;
                        else
                            updated++;
                        row.Saved();
                    }
                }
                var summary = RowEditLogic.SavedMessage(updated, deleted, inserted);
                if (inserted > 0)
                    summary += " — 새 행에서 비운 열(기본값·자동 번호)은 다시 조회해야 보입니다";
                AddMessage(dbId, summary + (stopped ? " — 오류가 난 행부터는 보내지 않았습니다(표시는 그대로)." : ""), stopped ? MessageKind.Error : MessageKind.Info);
                SetStatus(tab, session.IsBroken ? WorkspaceLogic.BrokenStatus(profile.Name) : WorkspaceLogic.TextStatus(stopped ? "오류 — 메시지 탭을 보세요. " + summary : summary));
                if (stopped)
                    ShowPane(tab, WorkspacePane.Messages);
                RefreshCursorStates(dbId);
            }
            catch (SessionBusyException)
            {
                SetStatus(tab, WorkspaceLogic.BusyOtherStatus(profile.Name));
            }
            finally
            {
                EndRun(tab);
                RenderEditBar();
            }
        }

        /// <summary>
        /// 오른쪽 메뉴 [INSERT 문 만들기]: 빠른 조회 결과에서 고른 행(화면 순서, 고친 값 그대로)의 INSERT 문을 클립보드에 넣는다.
        /// 기본 키가 없어 편집할 수 없는 테이블에서도 된다(테이블 이름과 결과 열만 쓴다).
        /// </summary>
        private void CopyInsertScript(ResultSetState result)
        {
            if (result == null || result.Edit == null || result.Grid == null || result.Columns == null)
                return;
            var rows = result.Grid.SelectedRows();
            if (rows.Count == 0)
                return;
            var profile = Profile(result.ResultDbId);
            var kind = profile != null ? DbProviders.For(profile).Kind : null;
            var script = RowEditLogic.InsertScript(result.Edit.Owner, result.Edit.Name, result.Columns, rows.Select(r => r.Values), kind);
            // 줄 끝은 \n 그대로: 바꾸면 글 값 안의 줄바꿈까지 바뀐다
            CopyText(script, "INSERT 문 " + rows.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + "개를 클립보드에 복사했습니다(" + WorkspaceLogic.QualifiedName(result.Edit.Owner, result.Edit.Name) + ").");
        }

        private static int SaveOrder(RowEditState state)
        {
            return state == RowEditState.Deleted ? 0 : state == RowEditState.Modified ? 1 : 2;
        }

        /// <summary>
        /// 빠른 조회 결과에 편집 대상을 붙인다: 데이터 사전의 기본 키 열을 결과 열에 맞춘다. 조회 전에 읽어 둔 열 정보(metadata)가 없으면 편집 불가로 표시.
        /// </summary>
        private void AttachEditTarget(SqlTabState tab, OracleConnectionProfile profile, string owner, string objectName, string sql, List<ColumnInfo> metadata, string metadataError, string where)
        {
            var result = tab.ActiveResult;
            if (result == null || result.Columns == null || !string.Equals(result.Sql, sql, StringComparison.Ordinal))
                return;
            var provider = DbProviders.For(profile);
            result.Edit = metadata == null
                ? RowEditLogic.Unavailable(owner, objectName, "열 정보를 읽지 못해 편집할 수 없습니다: " + (metadataError ?? ""))
                : RowEditLogic.Describe(owner, objectName, result.Columns, metadata, RowEditLogic.ParameterPrefix(provider.Kind));
            result.Edit.Where = where ?? "";
            if (result.Edit.CanEdit && result.Grid != null)
                result.Grid.EnableEditMarkers(result.Rows.Count);
            if (tab == _active)
                RenderEditBar();
        }

        /// <summary>조회 전에 열 정보(기본 키)를 읽는다. 실패하면 null(error에 이유) — 조회는 그대로 한다.</summary>
        private static async Task<List<ColumnInfo>> LoadColumnsAsync(DbSession session, OracleConnectionProfile profile, string owner, string objectName)
        {
            var provider = DbProviders.For(profile);
            return await session.QueryAsync(provider.Metadata.Columns(owner, objectName), provider.Metadata.ReadColumn);
        }
    }
}

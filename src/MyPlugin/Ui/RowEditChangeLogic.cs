using System;
using System.Collections.Generic;

namespace MyPlugin
{
    /// <summary>
    /// 행 편집 창의 변경 관리(순수 로직): 칸마다 기준 값과 지금 값을 비교해 강조·툴팁 "원래 값: …"·↶ 되돌리기에 쓴다.
    /// 기준 값은 언제나 ResultGridRow.Original(조회한 값) — 이미 고친 행을 다시 열어도 조회한 값과 비교하고, 되돌리면 그 값이 된다.
    /// 원래 값이 없는 새 행([행 추가]·[복제]·저장 전 새 행)은 창을 열 때 값을 기준으로 삼는다(툴팁 "처음 값: …").
    /// 값은 그리드 표시 문자열이고 null = NULL(빈 문자열 ""과 다르다). 편집할 수 없는 칸(LOB 등)은 되돌리지 않는다.
    /// </summary>
    internal sealed class RowEditChanges
    {
        private const int MaxToolTipLength = 500;

        private readonly string[] _baseline;
        private readonly string[] _opened;
        private readonly string[] _current;
        private readonly bool[] _editable;
        private readonly bool _insert;

        /// <param name="original">조회한 원래 값(ResultGridRow.OriginalValues). 새 행이면 null.</param>
        /// <param name="values">창을 열 때 값(고친 행이면 고친 값).</param>
        /// <param name="editable">열마다 고칠 수 있는지. 길이가 열 수.</param>
        /// <param name="insert">새 행 모드(빈 칸 = NULL).</param>
        public RowEditChanges(string[] original, string[] values, bool[] editable, bool insert)
        {
            _editable = editable ?? Array.Empty<bool>();
            _insert = insert;
            var count = _editable.Length;
            _opened = new string[count];
            for (var i = 0; i < count; i++)
                _opened[i] = Normalize(At(values, i));
            HasOriginal = original != null && !insert;
            _baseline = new string[count];
            for (var i = 0; i < count; i++)
                _baseline[i] = HasOriginal ? At(original, i) : _opened[i];
            _current = (string[])_opened.Clone();
        }

        /// <summary>기준이 조회한 원래 값이면 true. 새 행(기준 = 창을 열 때 값)이면 false.</summary>
        public bool HasOriginal { get; }

        public int Count
        {
            get { return _current.Length; }
        }

        /// <summary>창을 연 뒤 바꾼 칸이 있는지(닫기 확인용). 다시 연 행에서 이미 고쳐 둔 칸은 그대로 두면 변경이 아니다.</summary>
        public bool HasChanges
        {
            get
            {
                for (var i = 0; i < _current.Length; i++)
                {
                    if (_editable[i] && !string.Equals(_current[i], _opened[i], StringComparison.Ordinal))
                        return true;
                }
                return false;
            }
        }

        /// <summary>기준 값과 다른 칸 수(강조하는 칸).</summary>
        public int ChangedCount
        {
            get
            {
                var count = 0;
                for (var i = 0; i < _current.Length; i++)
                {
                    if (IsChanged(i))
                        count++;
                }
                return count;
            }
        }

        /// <summary>[모두 되돌리기]로 되돌릴 칸이 있는지.</summary>
        public bool CanRevertAny
        {
            get
            {
                for (var i = 0; i < _current.Length; i++)
                {
                    if (CanRevert(i))
                        return true;
                }
                return false;
            }
        }

        public string BaselineAt(int index)
        {
            return At(_baseline, index);
        }

        public string CurrentAt(int index)
        {
            return At(_current, index);
        }

        /// <summary>그 칸이 기준 값과 다른지(NULL과 빈 문자열은 다르다).</summary>
        public bool IsChanged(int index)
        {
            return index >= 0 && index < _current.Length && !string.Equals(_current[index], _baseline[index], StringComparison.Ordinal);
        }

        /// <summary>↶ 되돌리기를 보일지: 고칠 수 있는 칸이고 기준 값과 다를 때.</summary>
        public bool CanRevert(int index)
        {
            return index >= 0 && index < _editable.Length && _editable[index] && IsChanged(index);
        }

        /// <summary>칸의 지금 값을 넣는다(편집 창이 글·NULL 체크가 바뀔 때마다). 고칠 수 없는 칸은 무시.</summary>
        public void Set(int index, string value)
        {
            if (index < 0 || index >= _current.Length || !_editable[index])
                return;
            _current[index] = Normalize(value);
        }

        /// <summary>칸 하나를 기준 값으로 되돌리고 그 값을 돌려준다(편집 창이 칸에 다시 채운다).</summary>
        public string Revert(int index)
        {
            if (CanRevert(index))
                _current[index] = _baseline[index];
            return CurrentAt(index);
        }

        /// <summary>[모두 되돌리기]: 고칠 수 있는 칸을 모두 기준 값으로. 되돌린 칸의 위치를 돌려준다.</summary>
        public IList<int> RevertAll()
        {
            var reverted = new List<int>();
            for (var i = 0; i < _current.Length; i++)
            {
                if (!CanRevert(i))
                    continue;
                _current[i] = _baseline[i];
                reverted.Add(i);
            }
            return reverted;
        }

        /// <summary>바뀐 칸의 툴팁 "원래 값: …"(새 행은 "처음 값: …"). 바뀌지 않았으면 null.</summary>
        public string ToolTip(int index)
        {
            if (!IsChanged(index))
                return null;
            return (HasOriginal ? "원래 값: " : "처음 값: ") + Describe(_baseline[index]);
        }

        /// <summary>툴팁에 보일 값: NULL, (빈 문자열), 너무 길면 앞부분과 "…".</summary>
        public static string Describe(string value)
        {
            if (value == null)
                return "NULL";
            if (value.Length == 0)
                return "(빈 문자열)";
            return value.Length > MaxToolTipLength ? value.Substring(0, MaxToolTipLength) + "…" : value;
        }

        /// <summary>편집 창 칸의 값: NULL 체크면 null, 새 행은 빈 칸도 null, 그 밖은 글 그대로(빈 문자열 포함).</summary>
        public static string FieldValue(bool nullChecked, string text, bool insert)
        {
            if (nullChecked || (insert && string.IsNullOrEmpty(text)))
                return null;
            return text ?? "";
        }

        /// <summary>값을 칸에 채울 때 NULL 체크를 켤지. 새 행은 NULL도 빈 칸으로 보인다(바로 입력할 수 있게).</summary>
        public static bool ShowsAsNull(string value, bool insert)
        {
            return value == null && !insert;
        }

        // 새 행은 빈 칸을 NULL로 돌려주므로 빈 문자열과 NULL을 같게 본다(복제한 "" 칸이 열자마자 바뀐 칸이 되지 않게)
        private string Normalize(string value)
        {
            return _insert && value != null && value.Length == 0 ? null : value;
        }

        private static string At(string[] values, int index)
        {
            return values != null && index >= 0 && index < values.Length ? values[index] : null;
        }
    }
}

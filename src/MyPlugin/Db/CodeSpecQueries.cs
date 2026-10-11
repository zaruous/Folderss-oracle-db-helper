using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace MyPlugin
{
    /// <summary>
    /// GCM(공통 코드) 정의서의 조회: 사용자 SQL을 그대로 실행한다(플러그인은 SQL을 해석하지 않고 결과 열 별칭만 본다 — <see cref="CodeSpecContract"/>).
    /// [열 확인]은 SQL을 한 행만 읽게 감싸(세션의 <see cref="ISqlDialect"/> — Oracle은 ROWNUM) 열 이름만 읽는다(큰 조회가 다 돌지 않게). 끝의 ';'·'/' 줄은 떼어 낸다.
    /// </summary>
    internal static class CodeSpecQueries
    {
        /// <summary>사용자 SQL 정리: 앞뒤 공백, 끝의 ';'와 '/'만 있는 줄을 뗀다(편집기에서 그대로 복사한 글도 실행되게). 주석은 그대로 둔다.</summary>
        public static string Clean(string sql)
        {
            var text = (sql ?? "").Trim();
            while (true)
            {
                var before = text;
                text = text.TrimEnd();
                if (text.EndsWith(";", StringComparison.Ordinal))
                    text = text.Substring(0, text.Length - 1).TrimEnd();
                var lastLine = text.LastIndexOfAny(new[] { '\r', '\n' });
                var tail = lastLine < 0 ? text : text.Substring(lastLine + 1);
                if (tail.Trim() == "/" && lastLine >= 0)
                    text = text.Substring(0, lastLine).TrimEnd();
                if (text == before)
                    return text;
            }
        }

        /// <summary>열 확인용 SQL: 사용자 SQL(정리한 것)을 방언의 한 행 제한으로 감싼다. 사용자 SQL의 ORDER BY도 인라인 뷰 안에서 그대로 된다.</summary>
        public static string ProbeSql(string sql, ISqlDialect dialect)
        {
            if (dialect == null)
                throw new ArgumentNullException(nameof(dialect));
            return dialect.LimitOne(Clean(sql));
        }

        /// <summary>[열 확인]: 결과 열 이름만 읽어 별칭 계약과 맞춰 본다(세션의 방언으로 감싼다).</summary>
        public static async Task<CodeSpecCheck> ProbeAsync(DbSession session, string sql, CancellationToken token)
        {
            if (session == null)
                throw new ArgumentNullException(nameof(session));
            var table = await session.QueryTableAsync(ProbeSql(sql, session.Dialect), 0, token).ConfigureAwait(true);
            return CodeSpecContract.Check(table.Columns);
        }

        /// <summary>[만들기]: 사용자 SQL을 통째로 읽는다(모든 행). 별칭 검사는 부르는 쪽이 결과 열로 한다.</summary>
        public static Task<QueryTable> LoadAsync(DbSession session, string sql, CancellationToken token)
        {
            return session.QueryTableAsync(Clean(sql), null, token);
        }
    }
}

using System.Text;

namespace MyPlugin.UiTests
{
    /// <summary>
    /// 편집기 시험 데이터(docs/sql-editor-avalonedit-plan.md 5절): D1(경계 사례), D2(실무형), D3(크기별 생성 — 구성 비율 고정), D4-1(한 줄 1MB), D4-3(전부 주석).
    /// PoC 창에 있던 생성기를 3단계 교체 뒤 시험 쪽으로 옮겼다(같은 글).
    /// </summary>
    internal static class SqlSamples
    {
        public const string D1 = "-- 줄 주석 한글\n"
            + "SELECT 1 FROM DUAL; -- 끝 주석\n"
            + "/* 블록\n"
            + "   여러 줄 -- 안의 줄 주석 기호는 주석의 일부 */\n"
            + "SELECT /*+ INDEX(t IX_T) */ * FROM T t;          -- 힌트(블록 주석으로 칠함)\n"
            + "SELECT 'a -- 문자열 안' , 'it''s', '/* 아님 */' FROM DUAL;\n"
            + "SELECT q'[ -- 아님 ' 아님 ]', q'{ } }', q'( ) )', q'< >', q'! ! !', nq'[ 한글 ]', N'유니코드' FROM DUAL;\n"
            + "SELECT \"Col -- 아님\", \"a\"\"b\" FROM DUAL;\n"
            + "SELECT 'line1\n"
            + "line2 -- 여러 줄 문자열 안' FROM DUAL;\n"
            + "SELECT 1 - -1 FROM DUAL;                           -- 빼기·음수(주석 아님)\n"
            + "SELECT 10/2 FROM DUAL;                            -- 나눗셈(주석 아님)\n"
            + "SELECT '😀 이모지' FROM DUAL; -- 서로게이트 쌍: 오프셋 어긋남 확인\n"
            + "BEGIN\n"
            + "  NULL; -- PL/SQL 안 주석\n"
            + "END;\n"
            + "/\n"
            + "-- PostgreSQL 규칙으로 바꿔 보기\n"
            + "DO $$ BEGIN RAISE NOTICE 'x; -- y'; END $$;\n"
            + "SELECT E'it\\'s' FROM t; /* 바깥 /* 안쪽 */ 주석 */\n"
            + "-- SQL Server 규칙으로 바꿔 보기\n"
            + "SELECT [col -- x] FROM [t];\n"
            + "GO\n"
            + "SELECT '닫히지 않은 문자열 → 끝까지 문자열\n";

        // 실제 작업 스크립트 모양(조회·조건·조인·집계, 테이블 정의, DML, PL/SQL 배치, 한글 주석) — 키워드가 많은 글
        private static readonly string[] Work =
        {
            "-- ===== 주문 집계 (월 마감) =====",
            "SELECT o.order_id, o.order_date, c.customer_name AS \"고객명\", SUM(l.amount) AS total",
            "  FROM orders o",
            "  JOIN customers c ON c.customer_id = o.customer_id",
            "  LEFT OUTER JOIN order_lines l ON l.order_id = o.order_id",
            " WHERE o.order_date BETWEEN :from_date AND :to_date",
            "   AND o.status IN ('PAID', 'SHIPPED')",
            "   AND c.grade IS NOT NULL",
            " GROUP BY o.order_id, o.order_date, c.customer_name",
            "HAVING SUM(l.amount) > 1000",
            " ORDER BY total DESC;",
            "",
            "CREATE TABLE t_order_hist (",
            "  hist_id     NUMBER(12)      CONSTRAINT pk_order_hist PRIMARY KEY,",
            "  order_id    NUMBER(12)      NOT NULL REFERENCES orders(order_id),",
            "  changed_at  DATE            DEFAULT SYSDATE NOT NULL,",
            "  memo        VARCHAR2(4000)  -- 변경 메모",
            ");",
            "",
            "UPDATE orders SET status = 'CANCELLED', updated_at = SYSDATE WHERE order_id = 1001 AND status = 'PAID';",
            "INSERT INTO t_order_hist (hist_id, order_id, memo) VALUES (seq_hist.NEXTVAL, 1001, q'[고객 요청 '취소']');",
            "/* 아래 PL/SQL은",
            "   야간 배치에서 돈다 */",
            "BEGIN",
            "  FOR r IN (SELECT order_id FROM orders WHERE status = 'PAID') LOOP",
            "    UPDATE orders SET status = 'DONE' WHERE order_id = r.order_id;",
            "  END LOOP;",
            "  COMMIT;",
            "EXCEPTION",
            "  WHEN OTHERS THEN",
            "    ROLLBACK;",
            "    RAISE;",
            "END;",
            "/",
            ""
        };

        // 줄 주석 20%, 블록 주석 10%(3줄), 문자열 포함 30%(q'[…]' 포함), 일반 40%, 한글은 주석·문자열에 절반
        private static readonly string[] Unit =
        {
            "-- 주문 집계 주석 한글", "-- plain comment line",
            "/* 블록 주석 시작", "   두 번째 줄", "   세 번째 줄 */",
            "SELECT 'abc', '한글 문자열' FROM orders WHERE id = 1;", "SELECT q'[it's -- not]' FROM dual;", "UPDATE t SET name = 'x' WHERE id = 2;",
            "SELECT a, b, c FROM t1 JOIN t2 ON t1.id = t2.id;", "INSERT INTO t (a, b) VALUES (1, 2);", "SELECT COUNT(*) FROM orders;", "DELETE FROM t WHERE id = 3;"
        };

        public static string D2(int lines)
        {
            return Repeat(Work, lines);
        }

        public static string D3(int lines)
        {
            return Repeat(Unit, lines);
        }

        /// <summary>D4-1: 한 줄짜리 긴 INSERT … VALUES (…),(…),… (chars자 남짓).</summary>
        public static string D41(int chars)
        {
            var sb = new StringBuilder(chars + 64);
            sb.Append("INSERT INTO t_big (id, name, memo) VALUES ");
            for (var i = 1; sb.Length < chars; i++)
                sb.Append(i == 1 ? "" : ", ").Append('(').Append(i).Append(", '이름").Append(i).Append("', NULL)");
            return sb.Append(";\r\n").ToString();
        }

        /// <summary>D4-3: D3 전체가 블록 주석 하나(안의 */는 끊어 둔다).</summary>
        public static string D43(int lines)
        {
            return "/* 전부 주석\r\n" + D3(lines).Replace("*/", "* /") + "*/\r\n";
        }

        private static string Repeat(string[] unit, int lines)
        {
            var sb = new StringBuilder(lines * 48);
            for (var i = 0; i < lines; i++)
                sb.Append(unit[i % unit.Length]).Append("\r\n");
            return sb.ToString();
        }
    }
}

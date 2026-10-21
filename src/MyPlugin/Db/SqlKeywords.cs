using System;
using System.Collections.Generic;

namespace MyPlugin
{
    /// <summary>
    /// 구문 강조 키워드 목록(대소문자 무시, 영문만). 글 조각(s의 start부터 length자)을 문자열로 잘라 내지 않고 찾는다 —
    /// 10만 줄을 훑을 때 낱말마다 문자열을 만들지 않으려고.
    /// </summary>
    public sealed class SqlKeywords
    {
        private readonly Dictionary<int, List<string>> _byHash = new Dictionary<int, List<string>>();
        private readonly int _maxLength;

        public SqlKeywords(IEnumerable<string> words)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var word in words)
            {
                if (string.IsNullOrEmpty(word) || !seen.Add(word))
                    continue;
                var hash = Hash(word, 0, word.Length);
                List<string> bucket;
                if (!_byHash.TryGetValue(hash, out bucket))
                    _byHash[hash] = bucket = new List<string>();
                bucket.Add(word);
                _maxLength = Math.Max(_maxLength, word.Length);
            }
            Count = seen.Count;
        }

        public int Count { get; }

        public bool Contains(string word)
        {
            return word != null && Contains(word, 0, word.Length);
        }

        public bool Contains(string s, int start, int length)
        {
            if (length <= 0 || length > _maxLength)
                return false;
            var hash = Hash(s, start, length);
            if (hash == 0)
                return false;
            List<string> bucket;
            if (!_byHash.TryGetValue(hash, out bucket))
                return false;
            foreach (var word in bucket)
            {
                if (word.Length == length && string.Compare(s, start, word, 0, length, StringComparison.OrdinalIgnoreCase) == 0)
                    return true;
            }
            return false;
        }

        // 영문은 대문자로 접어 FNV-1a. 영문·숫자·_가 아닌 글자가 있으면 0(키워드가 아님 — 한글 낱말 등)
        private static int Hash(string s, int start, int length)
        {
            unchecked
            {
                var hash = (int)2166136261;
                for (var i = start; i < start + length; i++)
                {
                    var c = s[i];
                    if (c >= 'a' && c <= 'z')
                        c = (char)(c - 32);
                    else if (!(c >= 'A' && c <= 'Z') && !(c >= '0' && c <= '9') && c != '_')
                        return 0;
                    hash = (hash ^ c) * 16777619;
                }
                return hash == 0 ? 1 : hash;
            }
        }

        /// <summary>공백으로 나눈 낱말 묶음들을 하나로.</summary>
        internal static SqlKeywords Of(params string[] groups)
        {
            var words = new List<string>();
            foreach (var group in groups)
                words.AddRange(group.Split(new[] { ' ', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries));
            return new SqlKeywords(words);
        }

        // ---- 목록: 문장·절·조건·테이블 정의(DDL)·트랜잭션·블록, 그리고 자료형(테이블 정의가 읽히게). 함수 이름(COUNT·NVL …)은 넣지 않는다 ----

        internal const string Common =
            "SELECT FROM WHERE AND OR NOT IN IS NULL LIKE BETWEEN EXISTS AS ON JOIN INNER LEFT RIGHT FULL OUTER CROSS NATURAL USING "
            + "GROUP BY HAVING ORDER ASC DESC DISTINCT ALL ANY SOME UNION INTERSECT CASE WHEN THEN ELSE END "
            + "INSERT INTO VALUES UPDATE SET DELETE MERGE MATCHED CREATE ALTER DROP TRUNCATE RENAME TABLE VIEW INDEX SEQUENCE TRIGGER "
            + "PROCEDURE FUNCTION SCHEMA DATABASE MATERIALIZED PRIMARY FOREIGN KEY REFERENCES CONSTRAINT UNIQUE CHECK DEFAULT ADD COLUMN "
            + "WITH RECURSIVE GRANT REVOKE TO COMMIT ROLLBACK SAVEPOINT TRANSACTION LOCK BEGIN DECLARE IF WHILE FOR LOOP RETURN RETURNS "
            + "TRUE FALSE CAST OVER PARTITION ROWS RANGE UNBOUNDED PRECEDING FOLLOWING CURRENT ROW FETCH FIRST NEXT ONLY OFFSET NULLS LAST "
            + "CASCADE REPLACE COMMENT EXEC EXECUTE CURSOR OPEN CLOSE OUT "
            + "INT INTEGER SMALLINT BIGINT DECIMAL NUMERIC FLOAT REAL DOUBLE PRECISION CHAR CHARACTER VARCHAR DATE TIME TIMESTAMP INTERVAL ZONE BLOB CLOB";

        internal const string Oracle =
            "MINUS CONNECT PRIOR START NOCYCLE LEVEL ROWNUM ROWID SYSDATE SYSTIMESTAMP "
            + "NUMBER VARCHAR2 NVARCHAR2 NCHAR NCLOB RAW LONG BINARY_FLOAT BINARY_DOUBLE BOOLEAN PLS_INTEGER BINARY_INTEGER "
            + "PACKAGE BODY TYPE ROWTYPE ELSIF EXCEPTION RAISE PRAGMA OTHERS BULK COLLECT FORALL NOCOPY EXIT CONTINUE GOTO "
            + "SYNONYM PUBLIC TABLESPACE PURGE MODIFY ENABLE DISABLE NOVALIDATE IMMEDIATE RETURNING NOWAIT";

        internal const string Postgres =
            "LIMIT ILIKE SIMILAR RETURNING EXCEPT LATERAL DO LANGUAGE VACUUM ANALYZE EXPLAIN CONFLICT NOTHING EXTENSION OWNER "
            + "REFRESH CONCURRENTLY PERFORM RAISE NOTICE EXCEPTION ELSIF QUERY TABLESPACE ARRAY VARYING WITHOUT "
            + "SERIAL BIGSERIAL SMALLSERIAL TEXT BYTEA JSON JSONB UUID BOOLEAN TIMESTAMPTZ";

        internal const string SqlServer =
            "TOP GO PROC TRAN PRINT OUTPUT EXCEPT APPLY PIVOT UNPIVOT TRY CATCH THROW RAISERROR WAITFOR USE NOCOUNT "
            + "IDENTITY CLUSTERED NONCLUSTERED "
            + "NVARCHAR NCHAR NTEXT TEXT BIT TINYINT DATETIME DATETIME2 DATETIMEOFFSET SMALLDATETIME MONEY SMALLMONEY "
            + "UNIQUEIDENTIFIER VARBINARY BINARY IMAGE XML";
    }
}

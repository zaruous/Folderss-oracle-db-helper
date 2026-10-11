using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MyPlugin;
using Xunit;

namespace MyPlugin.Tests
{
    /// <summary>GCM 정의서: 별칭 계약 검사, 조회 결과 → 그룹(정렬·추가 속성), 템플릿 변수, JSON·CSV, 창 로직(설정·파일 이름·SQL 정리).</summary>
    public class CodeSpecTests
    {
        private static QueryTable Table(string[] columns, params string[][] rows)
        {
            var table = new QueryTable();
            table.Columns.AddRange(columns);
            foreach (var row in rows)
                table.Rows.Add(row);
            return table;
        }

        private static QueryTable Sample()
        {
            return Table(new[] { "TABLE_NAME", "GROUP_NAME", "CODE", "CODE_NAME", "CODE_DESC", "SORT_NO", "USE_YN", "PLANT_CD" },
                new[] { "ORDER_STATUS", "주문 상태", "B", "출고", "", "2", "Y", "P1" },
                new[] { "ORDER_STATUS", "", "A", "접수", "고객이 주문을 넣음", "1", "Y", "P1" },
                new[] { "ORDER_STATUS", "주문 상태(다른 행)", "Z", "취소", null, "10", "N", null },
                new[] { "YN", "예/아니오", "Y", "예", "", "", "", "" },
                new[] { "YN", "", "N", "아니오", "", "", "", "" });
        }

        // ---------- 별칭 계약 ----------

        [Fact]
        public void Check_RequiredOptionalExtra_IgnoresCaseAndSpaces()
        {
            var check = CodeSpecContract.Check(new[] { " table_name ", "Code", "CODE_NAME", "sort_no", "PLANT_CD", "plant_cd", "LANG", "" });

            Assert.True(check.Ok);
            Assert.Equal(new[] { "TABLE_NAME", "CODE", "CODE_NAME", "SORT_NO" }, check.Found.ToArray());
            Assert.Empty(check.Missing);
            Assert.Equal(new[] { "GROUP_NAME", "GROUP_DESC", "CODE_DESC", "USE_YN" }, check.MissingOptional.ToArray());
            Assert.Equal(new[] { "PLANT_CD", "LANG" }, check.Extra.ToArray());
            Assert.True(check.Has("sort_no"));
            Assert.Equal("필수 별칭이 모두 있습니다 · 선택 1개 · 추가 속성 2개(PLANT_CD, LANG)", check.Summary);
        }

        [Fact]
        public void Check_And_Build_AcceptOldGroupIdAlias()
        {
            // 1.3까지의 별칭 GROUP_ID는 TABLE_NAME으로 읽는다(저장한 SQL 호환)
            var check = CodeSpecContract.Check(new[] { "group_id", "CODE", "CODE_NAME" });
            Assert.True(check.Ok);
            Assert.Equal(new[] { "TABLE_NAME", "CODE", "CODE_NAME" }, check.Found.ToArray());
            Assert.Empty(check.Extra);

            var data = CodeSpecModel.Build(Table(new[] { "GROUP_ID", "CODE", "CODE_NAME" }, new[] { "G1", "a", "A" }, new[] { "G2", "b", "B" }), null, null);
            Assert.Equal(new[] { "G1", "G2" }, data.Groups.Select(g => g.Id).ToArray());
            Assert.Equal("TABLE_NAME", CodeSpecModel.CsvHeader(data)[0]);
        }

        [Fact]
        public void Check_MissingRequired_ListsThem()
        {
            var check = CodeSpecContract.Check(new[] { "TABLE_NAME", "CD", "NM" });

            Assert.False(check.Ok);
            Assert.Equal(new[] { "CODE", "CODE_NAME" }, check.Missing.ToArray());
            Assert.Equal("빠진 필수 별칭: CODE, CODE_NAME", check.Summary);
            Assert.Equal("빠진 필수 별칭: CODE, CODE_NAME — SELECT 목록에 AS 별칭을 붙이세요.", CodeSpecLogic.CheckText(check));
        }

        [Fact]
        public void Contract_HasThreeRequiredAliases()
        {
            Assert.Equal(new[] { "TABLE_NAME", "CODE", "CODE_NAME" }, CodeSpecContract.RequiredNames.ToArray());
            Assert.Equal(8, CodeSpecContract.Aliases.Count);
            Assert.True(CodeSpecContract.IsKnown("use_yn"));
            Assert.False(CodeSpecContract.IsKnown("PLANT_CD"));
        }

        // ---------- 조회 결과 → 그룹 ----------

        [Fact]
        public void Build_GroupsInFirstAppearanceOrder_SortsBySortNo_FirstNonEmptyGroupName()
        {
            var data = CodeSpecModel.Build(Sample(), "gcm.sql", "SELECT 1");

            Assert.Equal(new[] { "ORDER_STATUS", "YN" }, data.Groups.Select(g => g.Id).ToArray());
            var status = data.Groups[0];
            Assert.Equal("주문 상태", status.Name);
            Assert.Equal("", status.Description);
            // SORT_NO 1, 2, 10 — 숫자로 정렬(글자였다면 "1","10","2")
            Assert.Equal(new[] { "A", "B", "Z" }, status.Codes.Select(c => c.Code).ToArray());
            Assert.Equal("고객이 주문을 넣음", status.Codes[0].Description);
            Assert.Equal("", status.Codes[2].Description);
            Assert.True(status.Codes[0].IsUsed);
            Assert.False(status.Codes[2].IsUsed);
            Assert.Equal("P1", status.Codes[0].Attrs["plant_cd"]);
            Assert.Equal("", status.Codes[2].Attrs["PLANT_CD"]);
            // SORT_NO가 빈 값은 SQL 순서 유지
            Assert.Equal(new[] { "Y", "N" }, data.Groups[1].Codes.Select(c => c.Code).ToArray());
            Assert.True(data.Groups[1].Codes[0].IsUsed);

            Assert.Equal(5, data.Source.RowCount);
            Assert.Equal("gcm.sql", data.Source.Name);
            Assert.Equal(new[] { "PLANT_CD" }, data.Source.AttrNames.ToArray());
            Assert.Equal(5, data.CodeCount);
        }

        [Fact]
        public void Build_WithoutSortNo_KeepsSqlOrder_MixedSortValues()
        {
            var noSort = Table(new[] { "TABLE_NAME", "CODE", "CODE_NAME" }, new[] { "G", "3", "c" }, new[] { "G", "1", "a" }, new[] { "G", "2", "b" });
            Assert.Equal(new[] { "3", "1", "2" }, CodeSpecModel.Build(noSort, null, null).Groups[0].Codes.Select(c => c.Code).ToArray());

            // 숫자가 먼저(수로), 글자는 뒤(사전순), 빈 값은 맨 뒤, 같은 값은 SQL 순서
            var mixed = Table(new[] { "TABLE_NAME", "CODE", "CODE_NAME", "SORT_NO" },
                new[] { "G", "e", "", "" }, new[] { "G", "b", "", "B" }, new[] { "G", "n10", "", "10" }, new[] { "G", "a", "", "A" }, new[] { "G", "n2", "", "2" }, new[] { "G", "n2b", "", "2" });
            Assert.Equal(new[] { "n2", "n2b", "n10", "a", "b", "e" }, CodeSpecModel.Build(mixed, null, null).Groups[0].Codes.Select(c => c.Code).ToArray());
        }

        [Fact]
        public void Build_JsonObjectColumns_AreExpandedPerGroup()
        {
            // key_values·data_values: 그룹(코드 테이블)마다 키 이름이 다르다. PLANT는 보통 열
            var table = Table(new[] { "TABLE_NAME", "CODE", "CODE_NAME", "PLANT", "key_values", "data_values" },
                new[] { "AREA_CODE", "A1000", "Area1", "P1", "{\"AREA_CODE\": \"A1000\"}", "{\"COLOR_HEX_CODE\": \"#FF0000\", \"N\": 3, \"B\": true, \"Z\": null}" },
                new[] { "AREA_CODE", "A2000", "Area2", "P1", "{\"AREA_CODE\": \"A2000\"}", "{}" },
                new[] { "WMS_GI_STATUS", "0", "Not Shipped", "P1", "{\"STATUS\": \"0\"}", "{\"SHIP_FLAG\": \"Y\", \"NESTED\": {\"a\": 1}}" },
                new[] { "WMS_GI_STATUS", "1", "Shipped", "P1", null, "" });
            var data = CodeSpecModel.Build(table, null, null);

            Assert.Equal(new[] { "key_values", "data_values" }, data.Source.ExpandedColumns.ToArray());
            // 보통 열 먼저(열 순서), 그다음 JSON 키(처음 나온 순서)
            Assert.Equal(new[] { "PLANT", "AREA_CODE", "COLOR_HEX_CODE", "N", "B", "STATUS", "SHIP_FLAG", "NESTED" }, data.Source.AttrNames.ToArray());
            var area = data.Groups[0];
            Assert.Equal(new[] { "PLANT", "AREA_CODE", "COLOR_HEX_CODE", "N", "B" }, area.AttrNames.ToArray());
            Assert.Equal("A1000", area.Codes[0].Attrs["AREA_CODE"]);
            Assert.Equal("#FF0000", area.Codes[0].Attrs["COLOR_HEX_CODE"]);
            Assert.Equal("3", area.Codes[0].Attrs["N"]);
            Assert.Equal("true", area.Codes[0].Attrs["B"]);
            Assert.False(area.Codes[0].Attrs.ContainsKey("Z"));
            Assert.False(area.Codes[0].Attrs.ContainsKey("key_values"));
            Assert.False(area.Codes[1].Attrs.ContainsKey("COLOR_HEX_CODE"));
            var gi = data.Groups[1];
            Assert.Equal(new[] { "PLANT", "STATUS", "SHIP_FLAG", "NESTED" }, gi.AttrNames.ToArray());
            Assert.Equal("{\"a\": 1}", gi.Codes[0].Attrs["NESTED"]);
            Assert.Equal("P1", gi.Codes[1].Attrs["PLANT"]);
            Assert.False(gi.Codes[1].Attrs.ContainsKey("STATUS"));

            var context = CodeSpecModel.Context(new TableSpecDocument(), null, data);
            object value;
            context.TryGet("groups", out value);
            var group = (Dictionary<string, object>)((List<object>)value)[0];
            Assert.Equal("PLANT, AREA_CODE, COLOR_HEX_CODE, N, B", group["attrNamesText"]);
            var code = (Dictionary<string, object>)((List<object>)group["codes"])[0];
            Assert.Equal(new object[] { "P1", "A1000", "#FF0000", "3", "true" }, (List<object>)code["groupAttrValues"]);
            Assert.Equal(8, ((List<object>)code["attrValues"]).Count);
            Assert.Equal("PLANT=P1; AREA_CODE=A1000; COLOR_HEX_CODE=#FF0000; N=3; B=true", code["attrText"]);
            context.TryGet("source", out value);
            Assert.Equal("key_values, data_values", ((Dictionary<string, object>)value)["expandedColumnsText"]);

            // JSON은 그룹에 있는 속성만, CSV 머리글은 전체 속성
            var json = CodeSpecModel.Json(new TableSpecDocument(), null, data);
            using (var document = JsonDocument.Parse(json))
            {
                var first = document.RootElement.GetProperty("groups")[0];
                Assert.Equal(new[] { "PLANT", "AREA_CODE", "COLOR_HEX_CODE", "N", "B" }, first.GetProperty("attrNames").EnumerateArray().Select(e => e.GetString()).ToArray());
                Assert.False(first.GetProperty("codes")[0].GetProperty("attrs").TryGetProperty("STATUS", out _));
                Assert.Equal(new[] { "key_values", "data_values" }, document.RootElement.GetProperty("source").GetProperty("expandedColumns").EnumerateArray().Select(e => e.GetString()).ToArray());
            }
            Assert.StartsWith("TABLE_NAME,GROUP_NAME,GROUP_DESC,CODE,CODE_NAME,CODE_DESC,SORT_NO,USE_YN,PLANT,AREA_CODE,COLOR_HEX_CODE,N,B,STATUS,SHIP_FLAG,NESTED", CodeSpecModel.Csv(data));
        }

        [Fact]
        public void Build_NameValuePairColumns_AreExpandedPerGroup_PortableForm()
        {
            // 평범한 열 나열(모든 DBMS): KEY_1_NAME/KEY_1, DATA_1_NAME/DATA_1, DATA_2_NAME/DATA_2. PLANT는 보통 열
            var table = Table(new[] { "TABLE_NAME", "CODE", "CODE_NAME", "PLANT", "KEY_1_NAME", "KEY_1", "DATA_1_NAME", "DATA_1", "data_2_name", "data_2" },
                new[] { "AREA_CODE", "A1000", "Area1", "P1", "AREA_CODE", "A1000", "COLOR_HEX_CODE", "#FF0000", null, null },
                new[] { "AREA_CODE", "A2000", "Area2", "P1", "AREA_CODE", "A2000", "COLOR_HEX_CODE", null, "", "ignored" },
                new[] { "WMS_GI_STATUS", "0", "Not Shipped", "P1", "STATUS", "0", "SHIP_FLAG", "Y", "DONE_FLAG", "N" });
            var data = CodeSpecModel.Build(table, null, null);

            Assert.Equal(new[] { "KEY_1_NAME→KEY_1", "DATA_1_NAME→DATA_1", "data_2_name→data_2" }, data.Source.ExpandedColumns.ToArray());
            Assert.Equal(new[] { "PLANT", "AREA_CODE", "COLOR_HEX_CODE", "DATA_2", "STATUS", "SHIP_FLAG", "DONE_FLAG" }, data.Source.AttrNames.ToArray());
            var area = data.Groups[0];
            Assert.Equal(new[] { "PLANT", "AREA_CODE", "COLOR_HEX_CODE", "DATA_2" }, area.AttrNames.ToArray());
            Assert.Equal("A1000", area.Codes[0].Attrs["AREA_CODE"]);
            Assert.Equal("#FF0000", area.Codes[0].Attrs["COLOR_HEX_CODE"]);
            Assert.Equal("", area.Codes[1].Attrs["COLOR_HEX_CODE"]);
            // 이름이 빈 짝(data_2_name = "")은 값이 있으면 값 열 이름(대문자 DATA_2)으로 남고, 값도 없으면(1행) 뺀다. 이름 열·값 열 자체는 속성이 아니다
            Assert.Equal("ignored", area.Codes[1].Attrs["DATA_2"]);
            Assert.False(area.Codes[0].Attrs.ContainsKey("DATA_2"));
            Assert.Contains("DATA_2", area.Codes[1].Attrs.Keys);
            Assert.False(area.Codes[0].Attrs.ContainsKey("KEY_1"));
            Assert.False(area.Codes[0].Attrs.ContainsKey("KEY_1_NAME"));
            var gi = data.Groups[1];
            Assert.Equal(new[] { "PLANT", "STATUS", "SHIP_FLAG", "DONE_FLAG" }, gi.AttrNames.ToArray());
            Assert.Equal("N", gi.Codes[0].Attrs["DONE_FLAG"]);

            var context = CodeSpecModel.Context(new TableSpecDocument(), null, data);
            object value;
            context.TryGet("groups", out value);
            var group = (Dictionary<string, object>)((List<object>)value)[1];
            Assert.Equal("PLANT, STATUS, SHIP_FLAG, DONE_FLAG", group["attrNamesText"]);
            Assert.Equal(new object[] { "P1", "0", "Y", "N" }, (List<object>)((Dictionary<string, object>)((List<object>)group["codes"])[0])["groupAttrValues"]);
            context.TryGet("source", out value);
            Assert.Equal("KEY_1_NAME→KEY_1, DATA_1_NAME→DATA_1, data_2_name→data_2", ((Dictionary<string, object>)value)["expandedColumnsText"]);
        }

        [Fact]
        public void Build_ColumnNotAlwaysJson_StaysPlainAttr()
        {
            var table = Table(new[] { "TABLE_NAME", "CODE", "CODE_NAME", "NOTE" },
                new[] { "G", "A", "", "{\"X\": 1}" },
                new[] { "G", "B", "", "plain text" });
            var data = CodeSpecModel.Build(table, null, null);
            Assert.Empty(data.Source.ExpandedColumns);
            Assert.Equal(new[] { "NOTE" }, data.Source.AttrNames.ToArray());
            Assert.Equal("{\"X\": 1}", data.Groups[0].Codes[0].Attrs["NOTE"]);
        }

        [Fact]
        public void TryParseJsonObject_Rules()
        {
            List<KeyValuePair<string, string>> pairs;
            Assert.True(CodeSpecModel.TryParseJsonObject(" {\"a\": \"x\", \"b\": 2.5, \"c\": false, \"d\": null, \"e\": [1,2]} ", out pairs));
            Assert.Equal(new[] { "a=x", "b=2.5", "c=false", "e=[1,2]" }, pairs.Select(p => p.Key + "=" + p.Value).ToArray());
            Assert.False(CodeSpecModel.TryParseJsonObject("[1,2]", out pairs));
            Assert.False(CodeSpecModel.TryParseJsonObject("{broken", out pairs));
            Assert.False(CodeSpecModel.TryParseJsonObject("", out pairs));
            Assert.True(CodeSpecModel.TryParseJsonObject("{}", out pairs));
            Assert.Empty(pairs);
        }

        [Fact]
        public void Build_MissingRequired_Throws()
        {
            var ex = Assert.Throws<ArgumentException>(() => CodeSpecModel.Build(Table(new[] { "TABLE_NAME", "CODE" }), null, null));
            Assert.Contains("CODE_NAME", ex.Message);
        }

        [Fact]
        public void IsUsed_Variants()
        {
            foreach (var yes in new[] { "", " ", "Y", "y", "yes", "1", "true", "T", "사용" })
                Assert.True(new CodeEntry { UseYn = yes }.IsUsed, yes);
            foreach (var no in new[] { "N", "0", "false", "미사용", "X" })
                Assert.False(new CodeEntry { UseYn = no }.IsUsed, no);
        }

        [Fact]
        public void Filter_ByGroupId_OrNameAndDescription()
        {
            var data = CodeSpecModel.Build(Sample(), null, null);
            var pattern = new Regex("^ORDER", RegexOptions.IgnoreCase);
            Assert.Equal(new[] { "ORDER_STATUS" }, CodeSpecModel.Filter(data.Groups, pattern, false).Select(g => g.Id).ToArray());
            Assert.Equal(2, CodeSpecModel.Filter(data.Groups, null, false).Count);
            Assert.Empty(CodeSpecModel.Filter(data.Groups, new Regex("예/아니오"), false));
            Assert.Equal(new[] { "YN" }, CodeSpecModel.Filter(data.Groups, new Regex("예/아니오"), true).Select(g => g.Id).ToArray());
        }

        // ---------- 템플릿 변수 ----------

        [Fact]
        public void Context_Keys_AndValues()
        {
            var data = CodeSpecModel.Build(Sample(), "gcm.sql", "SELECT 1");
            var doc = new TableSpecDocument { Title = "  ", Project = "MES", Author = "홍길동", Date = new DateTime(2026, 10, 11) };
            var profile = new OracleConnectionProfile { Name = "개발", UserId = "mes", Host = "h", Port = 1521, ServiceName = "xe" };
            var context = CodeSpecModel.Context(doc, profile, data, "Oracle 19c");

            object value;
            Assert.True(context.TryGet("doc", out value));
            var docMap = (Dictionary<string, object>)value;
            Assert.Equal(CodeSpecModel.DefaultTitle, docMap["title"]);
            Assert.Equal(2, docMap["groupCount"]);
            Assert.Equal(5, docMap["codeCount"]);
            Assert.Equal("2026-10-11", docMap["date"]);

            Assert.True(context.TryGet("db", out value));
            Assert.Equal("Oracle 19c", ((Dictionary<string, object>)value)["version"]);

            Assert.True(context.TryGet("source", out value));
            var source = (Dictionary<string, object>)value;
            Assert.Equal("gcm.sql", source["name"]);
            Assert.Equal(5, source["rowCount"]);
            Assert.Equal("PLANT_CD", source["attrNamesText"]);
            Assert.Equal(new object[] { "PLANT_CD" }, (List<object>)source["attrNames"]);
            Assert.Contains("TABLE_NAME, GROUP_NAME", (string)source["columns"]);

            Assert.True(context.TryGet("groups", out value));
            var groups = (List<object>)value;
            var status = (Dictionary<string, object>)groups[0];
            Assert.Equal(1, status["no"]);
            Assert.Equal("ORDER_STATUS", status["sheetName"]);
            Assert.Equal(3, status["codeCount"]);
            var codes = (List<object>)status["codes"];
            var first = (Dictionary<string, object>)codes[0];
            Assert.Equal(1, first["no"]);
            Assert.Equal("A", first["code"]);
            Assert.Equal("접수", first["name"]);
            Assert.Equal("ORDER_STATUS", first["group"]);
            Assert.Equal("주문 상태", first["groupName"]);
            Assert.Equal(true, first["isUsed"]);
            Assert.Equal("P1", ((Dictionary<string, object>)first["attrs"])["plant_cd"]);
            Assert.Equal(new object[] { "P1" }, (List<object>)first["attrValues"]);
            Assert.Equal("PLANT_CD=P1", first["attrText"]);
            var third = (Dictionary<string, object>)codes[2];
            Assert.Equal("", third["attrText"]);
            Assert.Equal(false, third["isUsed"]);

            // 편 목록은 전체 순번
            Assert.True(context.TryGet("codes", out value));
            var flat = (List<object>)value;
            Assert.Equal(5, flat.Count);
            Assert.Equal(4, ((Dictionary<string, object>)flat[3])["no"]);
            Assert.Equal("YN", ((Dictionary<string, object>)flat[3])["group"]);
        }

        [Fact]
        public void Context_SheetNames_UniqueAndShort()
        {
            var table = Table(new[] { "TABLE_NAME", "CODE", "CODE_NAME" },
                new[] { "A_VERY_LONG_GROUP_IDENTIFIER_OVER_31_CHARS_1", "x", "" },
                new[] { "A_VERY_LONG_GROUP_IDENTIFIER_OVER_31_CHARS_2", "x", "" },
                new[] { "BAD[NAME]", "x", "" });
            var context = CodeSpecModel.Context(new TableSpecDocument(), null, CodeSpecModel.Build(table, null, null));
            object value;
            context.TryGet("groups", out value);
            var names = ((List<object>)value).Select(g => (string)((Dictionary<string, object>)g)["sheetName"]).ToList();
            Assert.Equal(3, names.Distinct(StringComparer.OrdinalIgnoreCase).Count());
            Assert.All(names, n => Assert.True(n.Length <= ExcelTemplate.MaxSheetNameLength));
            Assert.DoesNotContain(names, n => n.Contains("["));
        }

        [Fact]
        public void Variables_CoverContextKeys()
        {
            var data = CodeSpecModel.Build(Sample(), "s", "q");
            var context = CodeSpecModel.Context(new TableSpecDocument(), new OracleConnectionProfile { Name = "n" }, data, "v");
            var text = string.Join("\n", CodeSpecModel.Variables.Select(v => v.Name));
            object value;
            foreach (var root in new[] { "doc", "db", "source" })
            {
                context.TryGet(root, out value);
                foreach (var key in ((Dictionary<string, object>)value).Keys)
                    Assert.True(text.Contains("$" + root + "." + key), "$" + root + "." + key);
            }
            context.TryGet("groups", out value);
            var group = (Dictionary<string, object>)((List<object>)value)[0];
            foreach (var key in group.Keys)
                Assert.True(text.Contains("$group." + key), "$group." + key);
            foreach (var key in ((Dictionary<string, object>)((List<object>)group["codes"])[0]).Keys)
                Assert.True(text.Contains("$code." + key), "$code." + key);
        }

        // ---------- JSON · CSV ----------

        [Fact]
        public void Json_Structure_KoreanUnescaped()
        {
            var data = CodeSpecModel.Build(Sample(), "gcm.sql", "SELECT 1");
            var json = CodeSpecModel.Json(new TableSpecDocument { Title = "코드 정의서" }, new OracleConnectionProfile { Name = "개발", UserId = "u", Host = "h", Port = 1, ServiceName = "s" }, data, "19c");

            Assert.Contains("\"title\": \"코드 정의서\"", json);
            using (var document = JsonDocument.Parse(json))
            {
                var root = document.RootElement;
                Assert.Equal(2, root.GetProperty("doc").GetProperty("groupCount").GetInt32());
                Assert.Equal("19c", root.GetProperty("db").GetProperty("version").GetString());
                Assert.Equal("gcm.sql", root.GetProperty("source").GetProperty("name").GetString());
                Assert.Equal(5, root.GetProperty("source").GetProperty("rowCount").GetInt32());
                var groups = root.GetProperty("groups");
                Assert.Equal(2, groups.GetArrayLength());
                var first = groups[0];
                Assert.Equal("ORDER_STATUS", first.GetProperty("id").GetString());
                Assert.Equal("주문 상태", first.GetProperty("name").GetString());
                var code = first.GetProperty("codes")[0];
                Assert.Equal("A", code.GetProperty("code").GetString());
                Assert.Equal("접수", code.GetProperty("name").GetString());
                Assert.True(code.GetProperty("isUsed").GetBoolean());
                Assert.Equal("P1", code.GetProperty("attrs").GetProperty("PLANT_CD").GetString());
                Assert.Equal("", first.GetProperty("codes")[2].GetProperty("attrs").GetProperty("PLANT_CD").GetString());
            }
        }

        [Fact]
        public void Csv_HeaderFixedPlusAttrs_QuotesSpecials_Bom()
        {
            var table = Table(new[] { "TABLE_NAME", "CODE", "CODE_NAME", "NOTE" },
                new[] { "G", "A", "쉼표,포함", "줄\n바꿈" },
                new[] { "G", "B", "따옴표\"안\"", null });
            var data = CodeSpecModel.Build(table, null, null);
            var csv = CodeSpecModel.Csv(data);
            var lines = csv.Split(new[] { "\r\n" }, StringSplitOptions.None);

            Assert.Equal("TABLE_NAME,GROUP_NAME,GROUP_DESC,CODE,CODE_NAME,CODE_DESC,SORT_NO,USE_YN,NOTE", lines[0]);
            // 값 안의 LF는 줄 끝(CRLF)이 아니라 따옴표 안에 그대로
            Assert.Equal("G,,,A,\"쉼표,포함\",,,,\"줄\n바꿈\"", lines[1]);
            Assert.Equal("G,,,B,\"따옴표\"\"안\"\"\",,,,", lines[2]);
            Assert.Equal("", lines[3]);
            Assert.Equal(4, lines.Length);
            var bytes = CodeSpecModel.CsvBytes(data);
            Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes.Take(3).ToArray());
            Assert.Equal(csv, Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3));
        }

        // ---------- 창 로직 ----------

        [Fact]
        public void Settings_RoundTrip_KeepsSqlFilePerDb()
        {
            var previous = new CodeSpecSettings();
            previous.SqlFileByDb["db1"] = @"C:\a.sql";
            var document = new TableSpecDocument { Title = "T", Project = "P", Author = "A", Version = "2", Date = new DateTime(2026, 10, 11), Description = "D" };
            var settings = CodeSpecLogic.SettingsFrom(previous, document, "gcm-flat", null, @"C:\out", CodeSpecOutput.Json, "SELECT 1", "db2", @"C:\b.sql", "^ORD", true);
            var parsed = CodeSpecLogic.ParseSettings(CodeSpecLogic.SerializeSettings(settings));

            Assert.Equal("T", parsed.Title);
            Assert.Equal("2026-10-11", parsed.Date);
            Assert.Equal("gcm-flat", parsed.TemplateId);
            Assert.Null(parsed.TemplatePath);
            Assert.Equal("json", parsed.Output);
            Assert.Equal("SELECT 1", parsed.Sql);
            Assert.Equal("^ORD", parsed.GroupPattern);
            Assert.True(parsed.GroupPatternSearchName);
            Assert.Equal(@"C:\a.sql", parsed.SqlFileByDb["db1"]);
            Assert.Equal(@"C:\b.sql", parsed.SqlFileByDb["db2"]);

            // 파일 없이 만들면 그 접속의 기억을 지운다
            var cleared = CodeSpecLogic.SettingsFrom(parsed, document, "x", null, null, CodeSpecOutput.Excel, "", "db2", null, "", false);
            Assert.False(cleared.SqlFileByDb.ContainsKey("db2"));
            Assert.True(cleared.SqlFileByDb.ContainsKey("db1"));
            Assert.Equal("xlsx", cleared.Output);

            Assert.Null(CodeSpecLogic.ParseSettings("{broken"));
            Assert.Null(CodeSpecLogic.ParseSettings(""));
            Assert.NotNull(CodeSpecLogic.ParseSettings("{}").SqlFileByDb);
        }

        [Fact]
        public void DocumentFrom_DefaultsAndSaved()
        {
            var today = new DateTime(2026, 10, 11);
            var fresh = CodeSpecLogic.DocumentFrom(null, "me", today);
            Assert.Equal(CodeSpecModel.DefaultTitle, fresh.Title);
            Assert.Equal("me", fresh.Author);
            Assert.Equal(today, fresh.Date);

            var saved = CodeSpecLogic.DocumentFrom(new CodeSpecSettings { Title = "", Date = "2026.01.02", Version = "3" }, "me", today);
            Assert.Equal("", saved.Title);
            Assert.Equal(new DateTime(2026, 1, 2), saved.Date);
            Assert.Equal("3", saved.Version);
        }

        [Fact]
        public void InitialSql_FileThenLastSqlThenDefault()
        {
            string file;
            Assert.Equal(CodeSpecLogic.DefaultSql, CodeSpecLogic.InitialSql(null, "db", p => "x", out file));
            Assert.Null(file);

            var settings = new CodeSpecSettings { Sql = "SELECT last" };
            settings.SqlFileByDb["db"] = @"C:\gcm.sql";
            Assert.Equal("SELECT file", CodeSpecLogic.InitialSql(settings, "db", p => p == @"C:\gcm.sql" ? "SELECT file" : null, out file));
            Assert.Equal(@"C:\gcm.sql", file);

            // 파일을 읽지 못하면 마지막 본문
            Assert.Equal("SELECT last", CodeSpecLogic.InitialSql(settings, "db", p => { throw new FileNotFoundException(); }, out file));
            Assert.Null(file);
            Assert.Equal("SELECT last", CodeSpecLogic.InitialSql(settings, "other", p => "x", out file));
            Assert.Equal(CodeSpecLogic.DefaultSql, CodeSpecLogic.InitialSql(new CodeSpecSettings { Sql = " " }, "db", p => "x", out file));
        }

        [Fact]
        public void Output_NamesLabelsFilters()
        {
            Assert.Equal(CodeSpecOutput.Json, CodeSpecLogic.OutputFrom("JSON"));
            Assert.Equal(CodeSpecOutput.Csv, CodeSpecLogic.OutputFrom(" csv "));
            Assert.Equal(CodeSpecOutput.Excel, CodeSpecLogic.OutputFrom(null));
            Assert.Equal(CodeSpecOutput.Excel, CodeSpecLogic.OutputFrom("odd"));
            Assert.Equal("엑셀 (양식)", CodeSpecLogic.OutputLabel(CodeSpecOutput.Excel));
            Assert.StartsWith("CSV", CodeSpecLogic.SaveFilter(CodeSpecOutput.Csv));
            Assert.Equal("공통_코드_정의서_개발_DB_20261011.json", CodeSpecLogic.DefaultFileName("", "개발 DB", CodeSpecOutput.Json, new DateTime(2026, 10, 11)));
            Assert.Equal("GCM_20261011.xlsx", CodeSpecLogic.DefaultFileName("GCM", null, CodeSpecOutput.Excel, new DateTime(2026, 10, 11)));
        }

        [Fact]
        public void Texts()
        {
            Assert.Equal("그룹 12개 중 5개 · 코드 48개", CodeSpecLogic.FilterText(5, 12, 48, true));
            Assert.Equal("그룹 12개 · 코드 1,200개", CodeSpecLogic.FilterText(12, 12, 1200, false));
            Assert.Equal("그룹 2개 · 코드 5개로 CSV 정의서를 만들었습니다: C:\\x.csv", CodeSpecLogic.DoneText(2, 5, CodeSpecOutput.Csv, "C:\\x.csv"));
            // 0행·정규식 0그룹이면 만들지 않는다
            Assert.StartsWith("조회 결과가 0행입니다", CodeSpecLogic.EmptyText(0, 0, false));
            Assert.StartsWith("그룹 정규식에 맞는 그룹이 없습니다", CodeSpecLogic.EmptyText(10, 0, true));
            Assert.Null(CodeSpecLogic.EmptyText(10, 3, true));
            Assert.Null(CodeSpecLogic.EmptyText(10, 0, false));
            var prompt = CodeSpecLogic.LlmSqlPrompt();
            foreach (var alias in CodeSpecContract.Aliases)
                Assert.Contains(alias.Name, prompt);
            var llm = CodeSpecLogic.LlmPrompt("https://g/", "https://g/guide.md", @"C:\t", null);
            Assert.Contains("https://g/guide.md", llm);
            Assert.Contains(@"C:\t", llm);
        }

        [Fact]
        public void SqlFiles_ListsOnlySqlSorted_LabelWithoutExtension()
        {
            var folder = Path.Combine(Path.GetTempPath(), "dbh-codesql-" + Guid.NewGuid().ToString("N"));
            try
            {
                Assert.Empty(CodeSpecLogic.ListSqlFiles(folder));
                Directory.CreateDirectory(folder);
                File.WriteAllText(Path.Combine(folder, "b.sql"), "2");
                File.WriteAllText(Path.Combine(folder, "A.sql"), "1");
                File.WriteAllText(Path.Combine(folder, "c.txt"), "x");
                Assert.Equal(new[] { "A", "b" }, CodeSpecLogic.ListSqlFiles(folder).Select(CodeSpecLogic.SqlFileLabel).ToArray());
                Assert.Equal(Path.Combine("d", CodeSpecLogic.SqlFolderName), CodeSpecLogic.SqlFolder("d"));
                Assert.Null(CodeSpecLogic.SqlFolder(null));
            }
            finally
            {
                if (Directory.Exists(folder))
                    Directory.Delete(folder, true);
            }
        }

        // ---------- 조회 ----------

        [Fact]
        public void Clean_StripsTrailingSemicolonAndSlashLine()
        {
            Assert.Equal("SELECT 1 FROM DUAL", CodeSpecQueries.Clean("  SELECT 1 FROM DUAL ;  "));
            Assert.Equal("SELECT 1\nFROM DUAL", CodeSpecQueries.Clean("SELECT 1\nFROM DUAL;\n/\n"));
            Assert.Equal("SELECT 1\r\nFROM DUAL", CodeSpecQueries.Clean("SELECT 1\r\nFROM DUAL\r\n/"));
            // 가운데 ';'는 그대로(문자열 안 등)
            Assert.Equal("SELECT ';' FROM DUAL", CodeSpecQueries.Clean("SELECT ';' FROM DUAL;"));
            Assert.Equal("", CodeSpecQueries.Clean(null));
        }

        [Fact]
        public void ProbeSql_WrapsAsInlineViewWithRownum()
        {
            Assert.Equal("SELECT * FROM (\nSELECT a AS TABLE_NAME FROM t ORDER BY a\n) WHERE ROWNUM <= 1",
                CodeSpecQueries.ProbeSql("SELECT a AS TABLE_NAME FROM t ORDER BY a;", SqlDialects.Oracle));
            Assert.Equal("Oracle", SqlDialects.Oracle.Name);
        }

        [Fact]
        public void ProbeSql_GenericDialect_UsesLimit_NullDialectThrows()
        {
            Assert.Equal("SELECT * FROM (\nSELECT a AS TABLE_NAME FROM t\n) AS q LIMIT 1", CodeSpecQueries.ProbeSql("SELECT a AS TABLE_NAME FROM t;", SqlDialects.Generic));
            Assert.Equal("Generic", SqlDialects.Generic.Name);
            Assert.Throws<ArgumentNullException>(() => CodeSpecQueries.ProbeSql("SELECT 1", null));
        }

        [Fact]
        public void CodeSpecMenuText_ShowsTargetDb_EscapesUnderscore()
        {
            Assert.Equal("GCM 정의서 만들기 — 개발_DB(_G)…".Replace("개발_DB", "개발__DB"), ShellLogic.CodeSpecMenuText("개발_DB"));
            Assert.Equal("GCM 정의서 만들기(_G)… (트리에서 DB를 고르세요)", ShellLogic.CodeSpecMenuText(null));
            Assert.Equal("GCM 정의서 만들기(_G)… (트리에서 DB를 고르세요)", ShellLogic.CodeSpecMenuText(""));
        }

        [Fact]
        public void Templates_ListAndFolder()
        {
            Assert.Equal(2, CodeSpecTemplates.All.Count);
            Assert.Equal(CodeSpecTemplates.DefaultId, CodeSpecTemplates.Find(null, "nope").Id);
            Assert.Equal(CodeSpecTemplates.FlatId, CodeSpecTemplates.Find(CodeSpecTemplates.All, CodeSpecTemplates.FlatId).Id);
            Assert.Equal(Path.Combine("d", TableSpecTemplates.FolderName, CodeSpecTemplates.FolderName), CodeSpecTemplates.UserFolder("d"));
            Assert.Null(CodeSpecTemplates.UserFolder(null));
            Assert.Equal(CodeSpecTemplates.GuideUrl + "guide.md", CodeSpecTemplates.GuideMarkdownUrl);
        }
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using Vchem.Core.Contracts;
using Vchem.Core.Judge;
using Vchem.Core.Model;

namespace Vchem.JudgeRunner
{
    /// <summary>
    /// 脱离 Unity 的判定链跑例工具（文档 Tools/JudgeRunner）。
    /// 用法：
    ///   JudgeRunner <plan.json>              跑单个方案，打印 result.json
    ///   JudgeRunner --golden <目录>          批量跑黄金样例，逐条比对期望结果类
    ///   JudgeRunner --selftest               标准方案自检 + 同案两次判定一致性
    /// </summary>
    public static class Program
    {
        public static int Main(string[] args)
        {
            if (args.Length == 0)
            {
                PrintUsage();
                return 2;
            }

            try
            {
                if (args[0] == "--golden") return RunGolden(args.Length > 1 ? args[1] : "Tests/Golden");
                if (args[0] == "--selftest") return SelfTest();
                return RunOne(args[0]);
            }
            catch (Exception e)
            {
                Console.Error.WriteLine("ERROR: " + e);
                return 1;
            }
        }

        private static void PrintUsage()
        {
            Console.WriteLine("JudgeRunner - CO2 一期判定链命令行工具");
            Console.WriteLine("  JudgeRunner <plan.json>            跑单个方案");
            Console.WriteLine("  JudgeRunner --golden <目录>        批量跑黄金样例");
            Console.WriteLine("  JudgeRunner --selftest             标准方案自检 + 确定性检查");
        }

        private static int RunOne(string path)
        {
            string text = File.ReadAllText(path);
            Plan plan = Plan.FromJson(text);
            Outcome outcome = Resolver.Resolve(plan);
            Console.WriteLine(outcome.ToJson());
            return outcome.Errors.Count > 0 && outcome.Class == null ? 1 : 0;
        }

        private static int RunGolden(string dir)
        {
            if (!Directory.Exists(dir))
            {
                Console.Error.WriteLine("目录不存在: " + dir);
                return 1;
            }
            string[] files = Directory.GetFiles(dir, "*.plan.json");
            if (files.Length == 0)
            {
                Console.Error.WriteLine("没有找到 *.plan.json: " + dir);
                return 1;
            }
            Array.Sort(files, StringComparer.Ordinal);

            int pass = 0, fail = 0;
            foreach (string file in files)
            {
                string planText = File.ReadAllText(file);
                Plan plan = Plan.FromJson(planText);
                Outcome actual = Resolver.Resolve(plan);

                string expectPath = file.Replace(".plan.json", ".expect.json");
                string expectClass = null;
                List<string> expectErrors = null;
                Dictionary<string, object> expectQuantities = null;
                if (File.Exists(expectPath))
                {
                    Dictionary<string, object> ex = Json.ParseObject(File.ReadAllText(expectPath));
                    expectClass = Json.GetString(ex, "class", null);
                    expectQuantities = Json.GetObject(ex, "quantities");
                    object raw;
                    if (ex.TryGetValue("errors", out raw))
                    {
                        List<object> arr = raw as List<object>;
                        if (arr != null)
                        {
                            expectErrors = new List<string>();
                            foreach (object o in arr)
                            {
                                string s = o as string;
                                if (s != null) expectErrors.Add(s);
                            }
                        }
                    }
                }

                bool ok = true;
                if (expectClass != null)
                    ok = string.Equals(expectClass, actual.Class, StringComparison.Ordinal);
                if (expectErrors != null)
                    ok = ok && SameErrors(expectErrors, actual.Errors);
                if (expectQuantities != null)
                {
                    Dictionary<string, object> actualMap = Json.ParseObject(actual.ToJson());
                    ok = ok && MapSubset(expectQuantities, Json.GetObject(actualMap, "quantities"));
                }
                if (expectClass == null && expectErrors == null && expectQuantities == null)
                    ok = actual.Errors.Count > 0;

                // 确定性：同一 plan 跑两次，结果必须逐字节一致
                Outcome again = Resolver.Resolve(Plan.FromJson(planText));
                bool deterministic = actual.ToJson() == again.ToJson();

                if (ok && deterministic)
                {
                    pass++;
                    Console.WriteLine("PASS  " + Path.GetFileName(file) +
                        "  -> " + (actual.Class ?? string.Join(",", actual.Errors.ToArray())));
                }
                else
                {
                    fail++;
                    Console.WriteLine("FAIL  " + Path.GetFileName(file) +
                        "  expected=" + (expectClass ?? (expectErrors == null ? "-" : string.Join(",", expectErrors.ToArray()))) +
                        "  actual=" + (actual.Class ?? string.Join(",", actual.Errors.ToArray())) +
                        (deterministic ? "" : "  [NON-DETERMINISTIC]"));
                }
            }
            Console.WriteLine("---- golden: " + pass + " passed, " + fail + " failed ----");
            return fail == 0 ? 0 : 8;
        }

        /// <summary>错误码集合比较（顺序无关）。</summary>
        private static bool SameErrors(List<string> expected, List<string> actual)
        {
            if (expected == null || actual == null) return false;
            if (expected.Count != actual.Count) return false;
            List<string> a = new List<string>(expected);
            List<string> b = new List<string>(actual);
            a.Sort(StringComparer.Ordinal);
            b.Sort(StringComparer.Ordinal);
            for (int i = 0; i < a.Count; i++)
                if (!string.Equals(a[i], b[i], StringComparison.Ordinal)) return false;
            return true;
        }

        /// <summary>期望值是实际值的子集（只比对写出来的字段），数字允许 1e-6 误差。</summary>
        private static bool MapSubset(Dictionary<string, object> expect, Dictionary<string, object> actual)
        {
            if (actual == null) return false;
            foreach (KeyValuePair<string, object> kv in expect)
            {
                object av;
                if (!actual.TryGetValue(kv.Key, out av)) return false;
                if (kv.Value is double || kv.Value is int || kv.Value is long)
                {
                    double e = Convert.ToDouble(kv.Value, System.Globalization.CultureInfo.InvariantCulture);
                    double a = Convert.ToDouble(av, System.Globalization.CultureInfo.InvariantCulture);
                    if (Math.Abs(e - a) > 1e-6) return false;
                }
                else if (!string.Equals(kv.Value == null ? null : kv.Value.ToString(),
                                         av == null ? null : av.ToString(), StringComparison.Ordinal))
                {
                    return false;
                }
            }
            return true;
        }

        private static int SelfTest()
        {
            // 内建标准方案：正确装置 + 稀盐酸 + 向上排空气 + 木条 + 石灰水
            const string standard =
                "{\"schema\":\"vchem.plan/1\",\"experimentId\":\"exp.co2.prepare\"," +
                "\"topology\":{\"nodes\":[" +
                "{\"id\":\"n1\",\"type\":\"reactor.flask_conical\"}," +
                "{\"id\":\"n2\",\"type\":\"feed.thistle_funnel\",\"props\":{\"submerged\":true}}," +
                "{\"id\":\"n3\",\"type\":\"conduit.bent\"}," +
                "{\"id\":\"n4\",\"type\":\"collect.gas_jar\",\"props\":{\"orientation\":\"upright\",\"covered\":true}}" +
                "],\"edges\":[" +
                "{\"from\":\"n2.bottom\",\"to\":\"n1.mouth\",\"kind\":\"feed\"}," +
                "{\"from\":\"n3.in\",\"to\":\"n1.mouth\",\"kind\":\"gas\"}," +
                "{\"from\":\"n3.out\",\"to\":\"n4.mouth\",\"kind\":\"gas\",\"props\":{\"depth\":\"deep\"}}" +
                "]},\"verification\":{\"fullness\":\"burning_splint\",\"identity\":\"limewater\"}," +
                "\"feeds\":{\"solid\":\"reagent.caco3_lump\",\"liquid\":\"reagent.hcl_dilute\"}," +
                "\"studentId\":\"SELF\",\"submittedAt\":\"2026-11-19T00:00:00+08:00\"}";

            Outcome a = Resolver.Resolve(Plan.FromJson(standard));
            Outcome b = Resolver.Resolve(Plan.FromJson(standard));
            Console.WriteLine("standard class = " + (a.Class ?? "<error>") +
                "  outcome = " + a.Verdict);
            Console.WriteLine("determinism   = " + (a.ToJson() == b.ToJson() ? "OK" : "FAIL"));
            Console.WriteLine("result        = " + a.ToJson());
            return a.Class == "R-OK-01" && a.ToJson() == b.ToJson() ? 0 : 8;
        }
    }
}

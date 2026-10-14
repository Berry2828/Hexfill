using System;
using System.Globalization;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;

namespace HexFill
{
    public partial class HexFill
    {
        private const double HsetDefaultHplitCutSpacing = 0.1;
        private const double HsetDefaultHgapSmallGapTolerance = 0.5;
        private const double HsetDefaultHjoinMergeTolerance = 0.2;

        private static double HsetHplitCutSpacing = HsetDefaultHplitCutSpacing;
        private static double HsetHgapSmallGapTolerance = HsetDefaultHgapSmallGapTolerance;
        private static double HsetHjoinMergeTolerance = HsetDefaultHjoinMergeTolerance;

        private static double HsetHplitHalfCutSpacing
        {
            get { return HsetHplitCutSpacing / 2.0; }
        }

        [CommandMethod("HSET")]
        public void HSET()
        {
            Document document = Autodesk.AutoCAD.ApplicationServices.Application.DocumentManager.MdiActiveDocument;
            if (document == null) return;

            Editor editor = document.Editor;

            try
            {
                editor.WriteMessage("\n===== HexFill 当前设置 =====");
                WriteHsetCurrentValues(editor);

                PromptKeywordOptions options = new PromptKeywordOptions("\n请选择要设置的项目 [查看(V)/切割间距(S)/缺口阈值(G)/聚合距离(J)/恢复默认(D)] <查看>: ");
                options.Keywords.Add("V");
                options.Keywords.Add("S");
                options.Keywords.Add("G");
                options.Keywords.Add("J");
                options.Keywords.Add("D");
                options.Keywords.Default = "V";
                options.AllowNone = true;

                PromptResult result = editor.GetKeywords(options);
                if (result.Status == PromptStatus.Cancel) return;

                string keyword = result.Status == PromptStatus.None ? "V" : result.StringResult.ToUpperInvariant();
                if (keyword == "V")
                {
                    WriteHsetCurrentValues(editor);
                    return;
                }

                if (keyword == "D")
                {
                    HsetHplitCutSpacing = HsetDefaultHplitCutSpacing;
                    HsetHgapSmallGapTolerance = HsetDefaultHgapSmallGapTolerance;
                    HsetHjoinMergeTolerance = HsetDefaultHjoinMergeTolerance;
                    editor.WriteMessage("\nHSET：已恢复默认设置。");
                    WriteHsetCurrentValues(editor);
                    return;
                }

                if (keyword == "S")
                {
                    double value;
                    if (PromptHsetPositiveDouble(editor, "\n输入 HPLIT 分割间距(mm)", HsetHplitCutSpacing, out value))
                    {
                        HsetHplitCutSpacing = value;
                        editor.WriteMessage(string.Format("\nHSET：HPLIT 分割间距已设为 {0}mm。", HsetFormat(value)));
                    }
                    return;
                }

                if (keyword == "G")
                {
                    double value;
                    if (PromptHsetPositiveDouble(editor, "\n输入 HGAP 微小缺口阈值(mm)", HsetHgapSmallGapTolerance, out value))
                    {
                        HsetHgapSmallGapTolerance = value;
                        editor.WriteMessage(string.Format("\nHSET：HGAP 微小缺口阈值已设为 {0}mm。", HsetFormat(value)));
                    }
                    return;
                }

                if (keyword == "J")
                {
                    double value;
                    if (PromptHsetPositiveDouble(editor, "\n输入 HJOIN 聚合距离(mm)", HsetHjoinMergeTolerance, out value))
                    {
                        HsetHjoinMergeTolerance = value;
                        editor.WriteMessage(string.Format("\nHSET：HJOIN 聚合距离已设为 {0}mm。", HsetFormat(value)));
                    }
                }
            }
            catch (System.Exception ex)
            {
                editor.WriteMessage(string.Format("\nHSET错误：{0}", ex.Message));
            }
        }

        private static void WriteHsetCurrentValues(Editor editor)
        {
            editor.WriteMessage(string.Format("\nHPLIT 分割间距: {0}mm", HsetFormat(HsetHplitCutSpacing)));
            editor.WriteMessage(string.Format("\nHGAP 微小缺口阈值: {0}mm", HsetFormat(HsetHgapSmallGapTolerance)));
            editor.WriteMessage(string.Format("\nHJOIN 聚合距离: {0}mm", HsetFormat(HsetHjoinMergeTolerance)));
        }

        private static bool PromptHsetPositiveDouble(Editor editor, string message, double currentValue, out double value)
        {
            value = currentValue;
            PromptDoubleOptions options = new PromptDoubleOptions(string.Format("{0} <{1}>: ", message, HsetFormat(currentValue)));
            options.AllowNegative = false;
            options.AllowZero = false;
            options.AllowNone = true;
            options.DefaultValue = currentValue;

            PromptDoubleResult result = editor.GetDouble(options);
            if (result.Status == PromptStatus.Cancel) return false;
            if (result.Status == PromptStatus.None)
            {
                value = currentValue;
                return true;
            }
            if (result.Status != PromptStatus.OK) return false;

            if (!IsFinite(result.Value) || result.Value <= 0.0)
            {
                editor.WriteMessage("\n数值必须大于 0。");
                return false;
            }

            value = result.Value;
            return true;
        }

        private static string HsetFormat(double value)
        {
            return value.ToString("0.###", CultureInfo.InvariantCulture);
        }
    }
}

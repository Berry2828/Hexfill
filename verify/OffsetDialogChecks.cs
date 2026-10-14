using System;
using System.Globalization;
using System.Reflection;
using System.Windows.Forms;
using HexFill;

internal static class OffsetDialogChecks
{
    private static int checks;

    private static void Check(string name, bool passed)
    {
        if (!passed) throw new Exception(name);
        Console.WriteLine("PASS " + name);
        checks++;
    }

    private static T Field<T>(OffsetDialog dialog, string name)
    {
        return (T)typeof(OffsetDialog).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(dialog);
    }

    private static void Accept(OffsetDialog dialog)
    {
        typeof(OffsetDialog).GetMethod("BtnOK_Click", BindingFlags.Instance | BindingFlags.NonPublic)
            .Invoke(dialog, new object[] { null, EventArgs.Empty });
    }

    [STAThread]
    private static void Main()
    {
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        using (var dialog = new OffsetDialog())
        {
            dialog.CurrentLayerName = "Production";
            Accept(dialog);
            Check("current-layer-resolves-to-drawing-layer", dialog.SelectedLayerName == "Production");
            Check("valid-dialog-accepted", dialog.DialogResult == DialogResult.OK && dialog.OffsetDistance == 0.5);
        }
        using (var dialog = new OffsetDialog())
        {
            var layers = Field<ComboBox>(dialog, "cmbLayer");
            layers.Text = "  TypedDestination  ";
            Field<TextBox>(dialog, "txtDistance").Text = "2.75";
            Accept(dialog);
            Check("typed-layer-preserved", dialog.SelectedLayerName == "TypedDestination");
            Check("entered-distance-preserved", dialog.OffsetDistance == 2.75);
        }
        using (var dialog = new OffsetDialog())
        {
            dialog.CurrentLayerName = "Production";
            Field<ComboBox>(dialog, "cmbLayer").SelectedItem = "0";
            Accept(dialog);
            Check("explicit-zero-layer-preserved", dialog.SelectedLayerName == "0");
        }
        var parse = typeof(OffsetDialog).GetMethod("TryParseOffsetDistance", BindingFlags.Static | BindingFlags.NonPublic);
        foreach (string invalid in new[] { "NaN", "Infinity", "-Infinity", "0", "-2", "", "abc", "1e999" })
            Check("reject-distance-" + invalid, !(bool)parse.Invoke(null, new object[] { invalid, 0.0 }));
        foreach (string valid in new[] { "0.01", "5", "1e2" })
            Check("accept-distance-" + valid, (bool)parse.Invoke(null, new object[] { valid, 0.0 }));
        Console.WriteLine("Offset dialog: " + checks + " checks passed.");
    }
}

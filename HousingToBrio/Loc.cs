using System;
using System.Collections.Generic;

namespace HousingToBrio;

// English text is the key; anything without a translation shows in English.
// Same approach as SkyrimCompass/Loc.cs, so both plugins behave alike.
internal static class Loc
{
    public static bool Japanese { get; private set; }

    // Changes whenever the active language changes, so cached text can rebuild.
    public static int Revision { get; private set; }

    public static void Refresh(Configuration cfg, string? uiLanguage)
    {
        bool ja = cfg.Language switch
        {
            LanguageSetting.Japanese => true,
            LanguageSetting.English => false,
            _ => (uiLanguage ?? "").StartsWith("ja", StringComparison.OrdinalIgnoreCase),
        };
        if (ja == Japanese) return;
        Japanese = ja;
        Revision++;
    }

    public static string T(string en) => Japanese && Ja.TryGetValue(en, out var text) ? text : en;

    public static string F(string en, params object[] args) => string.Format(T(en), args);

    // ImGui label whose ID stays the same in every language ("###" hashes only what follows it).
    public static string L(string en, string id) => $"{T(en)}###{id}";

    private static readonly Dictionary<string, string> Ja = new()
    {
        ["...and more."] = "…ほか",
        ["1. Save as Brio Project"] = "1. 「Brioプロジェクトとして保存」を押す",
        ["2. Reload Brio (don't Save/Delete Project first)"] = "2. Brioを再読み込みする（先にBrioで Save / Delete Project を使わないでください）",
        ["3. Load Project in Brio"] = "3. Brioの「Load Project」で読み込む",
        ["All files (*.*)"] = "すべてのファイル (*.*)",
        ["Apartment"] = "アパルトメント",
        ["Apply dye colors"] = "染色を反映する",
        ["Auto"] = "自動",
        ["Auto-reload Brio after saving"] = "保存後にBrioを自動で再読み込みする",
        ["Brio folder not found - set manually."] = "Brioのフォルダが見つかりません。手動で指定してください。",
        ["Brio isn't enabled."] = "Brioが有効になっていません。",
        ["Brio not found."] = "Brioが見つかりません。",
        ["Browse..."] = "参照...",
        ["Converted from {0}"] = "{0} から変換",
        ["Couldn't load: {0}"] = "読み込めませんでした: {0}",
        ["Couldn't read Brio's registry: {0}"] = "Brioのプロジェクト一覧を読み取れませんでした: {0}",
        ["Description (optional)"] = "説明（任意）",
        ["Didn't disable in time - reload manually."] = "時間内に無効にできませんでした。手動で再読み込みしてください。",
        ["Didn't re-enable in time - enable manually."] = "時間内に有効に戻せませんでした。手動で有効にしてください。",
        ["Disabling Brio..."] = "Brioを無効にしています...",
        ["Exterior furniture: {0}"] = "屋外の庭具: {0}",
        ["Fixtures (skipped): {0}"] = "建材（変換対象外）: {0}",
        ["House size: {0}"] = "ハウスのサイズ: {0}",
        ["Housing To Brio (Use /housingtobrio OR /h2b to open this menu)"] = "Housing To Brio（/housingtobrio または /h2b でこのウィンドウを開けます）",
        ["Housing layout (*.json)"] = "ハウジングレイアウト (*.json)",
        ["In Load Project's options (gear icon): uncheck \"Relative Object Positions\"."] = "「Load Project」のオプション（歯車アイコン）で、「Relative Object Positions」のチェックを外してください。",
        ["Include exterior furniture"] = "屋外の庭具を含める",
        ["Include interior furniture"] = "室内の家具を含める",
        ["Interior furniture: {0}"] = "室内の家具: {0}",
        ["Item #{0}"] = "アイテム #{0}",
        ["Large"] = "Lサイズ",
        ["Layout file"] = "レイアウトファイル",
        ["Layout file not found."] = "レイアウトファイルが見つかりません。",
        ["Layout is {0}, you're in {1}."] = "このレイアウトは{0}用ですが、今いるのは{1}です。",
        ["Load a layout first."] = "先にレイアウトを読み込んでください。",
        ["Load layout"] = "レイアウトを読み込む",
        ["Medium"] = "Mサイズ",
        ["No furniture or fixtures found. Is this a ReMakePlace layout file?"] = "家具も建材も見つかりませんでした。ReMakePlaceのレイアウトファイルですか？",
        ["Not found - set manually."] = "見つかりません。手動で指定してください。",
        ["Opens the Housing To Brio window."] = "Housing To Brioのウィンドウを開きます。",
        ["Options"] = "オプション",
        ["Project name"] = "プロジェクト名",
        ["Re-detect"] = "再検出",
        ["Re-enabling Brio..."] = "Brioを有効に戻しています...",
        ["Reloaded - open Load Project."] = "再読み込みしました。「Load Project」を開いてください。",
        ["Save as Brio Project"] = "Brioプロジェクトとして保存",
        ["Save as a Brio Project"] = "Brioプロジェクトに書き出す",
        ["Save failed: {0}"] = "保存に失敗しました: {0}",
        ["Save verification failed - don't reload Brio."] = "保存の確認に失敗しました。Brioは再読み込みしないでください。",
        ["Saved as \"{0}\"."] = "「{0}」として保存しました。",
        ["Select Brio's config folder"] = "Brioの設定フォルダを選択してください",
        ["Select a housing layout file"] = "ハウジングのレイアウトファイルを選択",
        ["Set a valid Brio folder first."] = "先に正しいBrioのフォルダを指定してください。",
        ["Small"] = "Sサイズ",
        ["The selected file does not contain a valid housing layout."] = "選択したファイルは、有効なハウジングレイアウトではありません。",
        ["This file couldn't be read as a housing layout (invalid or unexpected JSON)."] = "このファイルはハウジングレイアウトとして読み取れませんでした（JSONが不正、または形式が違います）。",
        ["Verification failed: {0}"] = "確認に失敗しました: {0}",
        ["unknown"] = "不明",
        ["{0} item(s) skipped:"] = "{0}個のアイテムをスキップしました:",
        ["{0} {1} objects."] = "{0} オブジェクト数: {1}"
    };
}

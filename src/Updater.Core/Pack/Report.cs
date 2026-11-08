using System.Net;
using System.Text;

namespace Updater.Core;

/// <summary>Отчёт о сборке — обычная страница, открывается в любом браузере, без интернета.</summary>
public static class Report
{
    private static string E(string? s) => WebUtility.HtmlEncode(s ?? "");

    public static string Html(PackageManifest m, string root)
    {
        var sb = new StringBuilder();
        sb.Append("""
            <!doctype html><html lang="ru"><head><meta charset="utf-8">
            <meta name="viewport" content="width=device-width, initial-scale=1">
            <title>Updater — отчёт о сборке</title>
            <style>
            :root { --bg:#0E1013; --panel:#15181D; --raised:#1B1F25; --line:#232830; --text:#E9EDF2; --text2:#A3ACB8; --text3:#6B7480; --accent:#3B82F6; --ink:#8AB4FA; --warn:#F0BA62; --bad:#F2787C; --ok:#4CC38A; }
            @media (prefers-color-scheme: light) { :root { --bg:#F5F6F8; --panel:#FFFFFF; --raised:#EEF0F3; --line:#E1E4E9; --text:#15181D; --text2:#4B5563; --text3:#7B8494; --ink:#2563EB; --warn:#9A6412; --bad:#C42B31; --ok:#1E8A4C; } }
            * { box-sizing: border-box; }
            body { margin:0; background:var(--bg); color:var(--text); font:14px/1.55 "Segoe UI Variable Text","Segoe UI",system-ui,sans-serif; }
            main { max-width: 920px; margin: 0 auto; padding: 32px 20px 64px; }
            h1 { font: 700 26px/1.2 Bahnschrift, "Segoe UI", sans-serif; margin: 0 0 6px; letter-spacing: .2px; }
            h2 { font: 600 17px/1.3 Bahnschrift, "Segoe UI", sans-serif; margin: 32px 0 12px; }
            .muted { color: var(--text2); } .small { font-size: 12.5px; color: var(--text3); }
            .grid { display:grid; grid-template-columns: repeat(auto-fit, minmax(170px,1fr)); gap: 10px; margin-top: 18px; }
            .stat { background: var(--panel); border:1px solid var(--line); border-radius: 10px; padding: 12px 14px; }
            .stat b { display:block; font: 600 20px/1.2 "Cascadia Mono", Consolas, monospace; }
            table { width:100%; border-collapse: collapse; background: var(--panel); border:1px solid var(--line); border-radius:10px; overflow:hidden; }
            th, td { text-align:left; padding: 8px 12px; border-bottom: 1px solid var(--line); vertical-align: top; }
            th { font-weight:600; color: var(--text2); background: var(--raised); font-size: 12.5px; }
            tr:last-child td { border-bottom: 0; }
            td.num { text-align:right; white-space:nowrap; font-family: "Cascadia Mono", Consolas, monospace; font-size: 12.5px; }
            .chip { display:inline-block; padding: 1px 8px; border-radius: 6px; font-size: 12px; background: var(--raised); color: var(--text2); }
            .ok { color: var(--ok); } .warn { color: var(--warn); } .bad { color: var(--bad); }
            ol, ul { padding-left: 22px; } li { margin: 4px 0; }
            a { color: var(--ink); }
            details summary { cursor: pointer; color: var(--text2); }
            </style></head><body><main>
            """);
        sb.Append($"<h1>Сборка с {E(m.Source.Computer)}</h1>");
        sb.Append($"<div class=\"muted\">{E(m.Source.Windows)} · пользователь {E(m.Source.User)} · собрано {E(Fmt.Date(m.Finished ?? m.Created))}</div>");
        if (!m.IsComplete)
            sb.Append("<p class=\"bad\">Сборы не закончены — запусти программу ещё раз, она докопирует остальное.</p>");

        int auto = m.Programs.Count(p => p.AutoInstallable);
        sb.Append("<div class=\"grid\">");
        Stat(sb, Fmt.Size(m.TotalBytes), "в сборке");
        Stat(sb, m.Items.Sum(i => i.Files).ToString("N0", Fmt.Ru), "файлов");
        Stat(sb, $"{auto} из {m.Programs.Count}", "программ встанут сами");
        Stat(sb, (m.Windows?.Settings.Count ?? 0).ToString(), "настроек Windows");
        sb.Append("</div>");

        sb.Append("<h2>Что делать на новой винде</h2><ol>");
        sb.Append($"<li>Установи Windows, войди в систему и подключись к интернету.</li>");
        sb.Append($"<li>Запусти <b>{E(Package.ExeName)}</b> из этой папки — он сам найдёт сборку.</li>");
        sb.Append("<li>Проверь отмеченное и нажми «Начать переезд»: драйверы, программы, файлы и настройки встанут сами.</li>");
        sb.Append("<li>Пройди по списку «Не забыть» ниже.</li></ol>");

        if (m.Reminders.Count > 0)
        {
            sb.Append("<h2>Не забыть</h2><ul>");
            foreach (var r in m.Reminders) sb.Append($"<li>{E(r)}</li>");
            sb.Append("</ul>");
        }

        foreach (var group in m.Items.GroupBy(i => i.Section).OrderBy(g => g.Key))
        {
            sb.Append($"<h2>{E(SectionTitle(group.Key))}</h2><table><tr><th>Что</th><th>Где в сборке</th><th style=\"text-align:right\">Размер</th></tr>");
            foreach (var i in group)
            {
                var status = i.FailedCount > 0 ? $" <span class=\"warn\">· {i.FailedCount} не скопировано</span>" : "";
                var secret = i.Sensitive ? " <span class=\"chip\">секреты</span>" : "";
                sb.Append($"<tr><td>{E(i.Title)}{secret}{status}");
                if (i.Failed.Count > 0)
                {
                    sb.Append("<details><summary class=\"small\">какие файлы</summary><div class=\"small\">");
                    foreach (var f in i.Failed.Take(20)) sb.Append(E(f)).Append("<br>");
                    sb.Append("</div></details>");
                }
                var size = i.Files == 0 && i.RegFiles.Count > 0 ? "реестр" : Fmt.Size(i.Bytes);
                sb.Append($"</td><td class=\"small\">{E(i.Stored)}</td><td class=\"num\">{E(size)}</td></tr>");
            }
            sb.Append("</table>");
        }

        var autoList = m.Programs.Where(p => p.AutoInstallable).ToList();
        var manual = m.Programs.Where(p => !p.AutoInstallable).ToList();
        var onFlash = m.Programs.Where(p => p.LocalInstaller is not null).ToList();
        if (onFlash.Count > 0)
        {
            sb.Append($"<h2>Установщики на флешке ({onFlash.Count})</h2><p class=\"muted\">Поставятся даже без интернета: ");
            sb.Append(string.Join(", ", onFlash.Select(p => E(p.Name))));
            sb.Append("</p>");
        }
        if (m.Programs.Count > 0)
        {
            sb.Append($"<h2>Программы встанут сами ({autoList.Count})</h2><p class=\"muted\">");
            sb.Append(string.Join(", ", autoList.Select(p => E(p.Name))));
            sb.Append("</p>");
        }
        if (manual.Count > 0)
        {
            sb.Append($"<h2>Поставить вручную ({manual.Count})</h2><table><tr><th>Программа</th><th>Где взять</th></tr>");
            foreach (var p in manual)
            {
                var where = p.OffSystemDrive ? "лежит не на диске C: — возможно, хватит запустить с того же места"
                    : p.Url is not null ? $"<a href=\"{E(p.Url)}\">{E(p.Url)}</a>"
                    : $"<a href=\"https://www.google.com/search?q={WebUtility.UrlEncode(p.Name + " скачать")}\">найти в интернете</a>";
                sb.Append($"<tr><td>{E(p.Name)}<div class=\"small\">{E(p.Publisher)}</div></td><td>{where}</td></tr>");
            }
            sb.Append("</table>");
        }
        if (m.StoreApps.Count > 0)
        {
            sb.Append($"<h2>Из Microsoft Store ({m.StoreApps.Count})</h2><ul>");
            foreach (var a in m.StoreApps)
                sb.Append($"<li><a href=\"ms-windows-store://pdp/?PFN={E(a.FamilyName)}\">{E(a.Name)}</a></li>");
            sb.Append("</ul>");
        }

        sb.Append($"<p class=\"small\" style=\"margin-top:40px\">{E(m.App)} · сборка лежит в {E(root)}</p>");
        sb.Append("</main></body></html>");
        return sb.ToString();
    }

    private static void Stat(StringBuilder sb, string value, string label) =>
        sb.Append($"<div class=\"stat\"><b>{E(value)}</b><span class=\"muted\">{E(label)}</span></div>");

    public static string SectionTitle(Section s) => s switch
    {
        Section.Files => "Файлы",
        Section.Programs => "Программы",
        Section.Apps => "Настройки программ",
        Section.Games => "Сохранения игр",
        Section.Windows => "Windows",
        _ => "Важно",
    };
}

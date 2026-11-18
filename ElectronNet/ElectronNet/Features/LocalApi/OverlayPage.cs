namespace ElectronNet.Features.LocalApi;

/// <summary>
/// OBS 浏览器源用的叠加层：当前游戏、今日时长、成就进度。背景透明，样式由地址参数控制：
/// theme=dark|light、accent=%23RRGGBB、show=game,today,achievements,player、lang=zh|en、align=left|right、image=0|1。
/// 页面只请求同源的 /overlay/data，CSP 不允许其他连接；游戏横幅只从 Steam CDN 加载。
/// </summary>
internal static class OverlayPage
{
    public static readonly IReadOnlyDictionary<string, string> Headers = new Dictionary<string, string>
    {
        ["Content-Security-Policy"] = "default-src 'none'; style-src 'unsafe-inline'; script-src 'unsafe-inline'; connect-src 'self'; " +
                                      "img-src https://cdn.akamai.steamstatic.com https://shared.akamai.steamstatic.com https://cdn.cloudflare.steamstatic.com; " +
                                      "base-uri 'none'; form-action 'none'; frame-ancestors 'none'"
    };

    public const string Html = """
        <!doctype html>
        <html>
        <head>
        <meta charset="utf-8">
        <meta name="viewport" content="width=device-width, initial-scale=1">
        <title>Steam Stat overlay</title>
        <style>
          :root { --accent: #66c0f4; --fg: #f5f7fa; --muted: rgba(245,247,250,.68); --card: rgba(16,20,28,.78); --track: rgba(255,255,255,.16); }
          :root[data-theme=light] { --fg: #11151c; --muted: rgba(17,21,28,.62); --card: rgba(255,255,255,.86); --track: rgba(0,0,0,.12); }
          html, body { margin: 0; background: transparent; font-family: "Segoe UI", "PingFang SC", "Microsoft YaHei", "Noto Sans CJK SC", system-ui, sans-serif; color: var(--fg); }
          body { display: flex; padding: 12px; }
          body[data-align=right] { justify-content: flex-end; }
          .card { display: flex; gap: 14px; align-items: center; min-width: 280px; max-width: 560px; padding: 12px 16px; border-radius: 14px; background: var(--card); box-shadow: 0 6px 24px rgba(0,0,0,.25); border-left: 4px solid var(--accent); }
          .art { width: 138px; height: 64px; border-radius: 8px; object-fit: cover; flex: none; }
          .body { min-width: 0; flex: 1; }
          .label { font-size: 12px; letter-spacing: .06em; text-transform: uppercase; color: var(--muted); }
          .game { font-size: 20px; font-weight: 700; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
          .row { display: flex; gap: 14px; margin-top: 4px; font-size: 14px; color: var(--muted); font-variant-numeric: tabular-nums; }
          .row b { color: var(--fg); font-weight: 600; }
          .bar { height: 6px; margin-top: 8px; border-radius: 3px; background: var(--track); overflow: hidden; }
          .bar i { display: block; height: 100%; background: var(--accent); }
          [hidden] { display: none !important; }
        </style>
        </head>
        <body>
        <div class="card" id="card" hidden>
          <img class="art" id="art" alt="" hidden>
          <div class="body">
            <div class="label" id="label"></div>
            <div class="game" id="game"></div>
            <div class="row">
              <span id="session"></span>
              <span id="today"></span>
              <span id="ach"></span>
            </div>
            <div class="bar" id="bar" hidden><i id="fill"></i></div>
          </div>
        </div>
        <script>
        (() => {
          const params = new URLSearchParams(location.search);
          const token = params.get('token') || '';
          const show = new Set((params.get('show') || 'game,today,achievements').split(','));
          const accent = params.get('accent');
          if (accent && /^#[0-9a-fA-F]{3,8}$/.test(accent)) document.documentElement.style.setProperty('--accent', accent);
          document.documentElement.dataset.theme = params.get('theme') === 'light' ? 'light' : 'dark';
          document.body.dataset.align = params.get('align') === 'right' ? 'right' : 'left';
          let zh = params.has('lang') ? params.get('lang').startsWith('zh') : null;
          const T = {
            zh: { playing: '正在玩', idle: '今天', today: '今日', session: '本局', ach: '成就', nothing: '没有在玩', player: (p) => p + ' 正在玩', streamer: '玩家正在玩' },
            en: { playing: 'Now playing', idle: 'Today', today: 'Today', session: 'Session', ach: 'Achievements', nothing: 'Not playing', player: (p) => p + ' is playing', streamer: 'Now playing' }
          };
          const $ = (id) => document.getElementById(id);
          const fmt = (s) => {
            const m = Math.max(0, Math.floor(s / 60));
            return zh ? (m >= 60 ? Math.floor(m / 60) + ' 小时 ' + (m % 60) + ' 分' : m + ' 分钟') : (m >= 60 ? Math.floor(m / 60) + 'h ' + String(m % 60).padStart(2, '0') + 'm' : m + 'm');
          };
          let state = null, offset = 0;
          function render() {
            if (!state) return;
            const t = zh ? T.zh : T.en;
            const now = Date.now() / 1000 + offset;
            const game = state.playing[0];
            const running = game ? Math.max(0, now - game.startedAt) : 0;
            $('card').hidden = false;
            $('label').textContent = game ? (show.has('player') ? (state.streamerMode || !game.player ? t.streamer : t.player(game.player)) : t.playing) : t.idle;
            $('game').textContent = game ? (show.has('game') ? (game.name || '') : '') : t.nothing;
            $('game').hidden = !!game && !show.has('game');
            $('session').innerHTML = game ? t.session + ' <b>' + fmt(running) + '</b>' : '';
            // 今日时长里已经包含进行中的会话，在玩的时候才在本地每秒往上加
            $('today').innerHTML = show.has('today') ? t.today + ' <b>' + fmt(state.todaySeconds + (game ? now - state.now : 0)) + '</b>' : '';
            const a = game && game.achievements;
            $('ach').innerHTML = show.has('achievements') && a ? t.ach + ' <b>' + a.unlocked + '/' + a.total + '</b>' : '';
            $('bar').hidden = !(show.has('achievements') && a);
            if (a) $('fill').style.width = Math.round(a.unlocked / a.total * 100) + '%';
            const art = $('art');
            if (game && show.has('game') && params.get('image') !== '0') {
              const src = 'https://cdn.akamai.steamstatic.com/steam/apps/' + game.appId + '/header.jpg';
              if (art.dataset.src !== src) { art.dataset.src = src; art.hidden = false; art.src = src; }
            } else { art.hidden = true; art.dataset.src = ''; }
          }
          $('art').addEventListener('error', () => { $('art').hidden = true; });
          async function poll() {
            try {
              const response = await fetch('/overlay/data?token=' + encodeURIComponent(token), { cache: 'no-store' });
              if (response.ok) {
                state = await response.json();
                offset = state.now - Date.now() / 1000;
                if (zh === null) zh = String(state.language || '').toLowerCase().startsWith('zh');
                render();
              }
            } catch (e) { /* 应用没在运行时保持上一次的画面 */ }
          }
          poll();
          setInterval(poll, 5000);
          setInterval(render, 1000);
        })();
        </script>
        </body>
        </html>
        """;
}

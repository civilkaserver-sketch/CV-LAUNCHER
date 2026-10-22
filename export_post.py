import json
import base64
import os
import re

def main():
    root_dir = os.path.dirname(os.path.abspath(__file__))
    news_file = os.path.join(root_dir, 'news.json')
    
    with open(news_file, 'r', encoding='utf-8') as f:
        posts = json.load(f)
    post = posts[0]

    def embed_image(url):
        if not url:
            return ''
        clean_url = url.split('?')[0].replace('/', os.sep)
        full_path = os.path.join(root_dir, clean_url) if not os.path.isabs(clean_url) else clean_url
        if os.path.exists(full_path):
            ext = os.path.splitext(full_path)[1].lower()
            mime = 'image/png'
            if ext == '.svg':
                mime = 'image/svg+xml'
            elif ext in ('.jpg', '.jpeg'):
                mime = 'image/jpeg'
            with open(full_path, 'rb') as img_f:
                b64 = base64.b64encode(img_f.read()).decode('utf-8')
            return f'data:{mime};base64,{b64}'
        return url

    def fmt(text):
        if not text:
            return ''
        t = str(text)
        t = t.replace('&', '&amp;').replace('<', '&lt;').replace('>', '&gt;')
        t = re.sub(r'\*\*(.*?)\*\*', r'<strong>\1</strong>', t)
        t = re.sub(r'\*(.*?)\*', r'<em>\1</em>', t)
        t = re.sub(r'`([^`]+)`', r'<code>\1</code>', t)
        return t

    banner_url = ''
    if isinstance(post.get('banner'), dict):
        banner_path = post['banner'].get('fallback') or post['banner'].get('url')
        banner_url = embed_image(banner_path)

    html = []
    html.append('''<!DOCTYPE html>
<html lang="ru">
<head>
<meta charset="UTF-8">
<meta name="viewport" content="width=device-width, initial-scale=1.0">
<title>Civilization Reborn — ''' + post.get('title', 'Обновление') + '''</title>
<style>
  :root {
    --bg: #09090b;
    --card: #121216;
    --card-border: #22222a;
    --text: #f4f4f6;
    --muted: #a1a1aa;
    --dim: #71717a;
    --accent: #34d399;
    --accent-glow: rgba(52, 211, 153, 0.12);
    --radius: 14px;
  }
  * { box-sizing: border-box; margin: 0; padding: 0; }
  body {
    background: var(--bg);
    color: var(--text);
    font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, Helvetica, Arial, sans-serif;
    line-height: 1.6;
    padding: 32px 16px 80px;
  }
  .container {
    max-width: 880px;
    margin: 0 auto;
  }
  .header-card {
    background: var(--card);
    border: 1px solid var(--card-border);
    border-radius: var(--radius);
    overflow: hidden;
    margin-bottom: 24px;
    box-shadow: 0 10px 35px rgba(0,0,0,0.5);
  }
  .banner-wrap {
    width: 100%;
    max-height: 280px;
    overflow: hidden;
    background: #000;
  }
  .banner-wrap img {
    width: 100%;
    height: 100%;
    object-fit: cover;
    display: block;
  }
  .header-content {
    padding: 24px 28px;
  }
  .meta-row {
    display: flex;
    align-items: center;
    gap: 12px;
    margin-bottom: 12px;
  }
  .badge {
    font-size: 11px;
    font-weight: 700;
    text-transform: uppercase;
    letter-spacing: 1.5px;
    padding: 3px 10px;
    border-radius: 6px;
    background: var(--accent-glow);
    color: var(--accent);
    border: 1px solid rgba(52, 211, 153, 0.28);
  }
  .date {
    font-size: 13px;
    color: var(--dim);
  }
  h1 {
    font-size: 28px;
    font-weight: 800;
    letter-spacing: -0.5px;
    margin-bottom: 8px;
    color: #fff;
  }
  .subtitle {
    font-size: 14.5px;
    color: var(--muted);
    line-height: 1.6;
  }
  .section-card {
    background: var(--card);
    border: 1px solid var(--card-border);
    border-radius: var(--radius);
    padding: 24px 28px;
    margin-bottom: 22px;
    box-shadow: 0 4px 20px rgba(0,0,0,0.3);
  }
  .section-title {
    font-size: 19px;
    font-weight: 700;
    color: #fff;
    margin-bottom: 10px;
    display: flex;
    align-items: center;
    gap: 10px;
  }
  .section-desc {
    font-size: 14px;
    color: var(--muted);
    margin-bottom: 14px;
    line-height: 1.6;
  }
  .items-list {
    display: flex;
    flex-direction: column;
    gap: 8px;
    margin-bottom: 14px;
  }
  .item-row {
    font-size: 13.5px;
    color: #d4d4d8;
    padding-left: 18px;
    position: relative;
    line-height: 1.65;
  }
  .item-row::before {
    content: '—';
    position: absolute;
    left: 0;
    color: var(--dim);
  }
  code {
    background: #1c1c24;
    border: 1px solid #2e2e38;
    color: #e4e4e7;
    padding: 2px 6px;
    border-radius: 4px;
    font-size: 12px;
    font-family: ui-monospace, SFMono-Regular, Consolas, monospace;
  }
  strong {
    color: #fff;
    font-weight: 600;
  }
  .media-plate {
    margin: 16px 0 12px;
    border-radius: 10px;
    overflow: hidden;
    border: 1px solid var(--card-border);
    background: #000;
    cursor: zoom-in;
    position: relative;
    box-shadow: 0 4px 16px rgba(0,0,0,0.4);
    transition: transform 0.2s, border-color 0.2s;
  }
  .media-plate:hover {
    transform: scale(1.008);
    border-color: var(--accent);
  }
  .media-plate img {
    width: 100%;
    display: block;
    aspect-ratio: 16 / 9;
    object-fit: cover;
  }
  .subsection {
    margin-top: 22px;
    padding-top: 18px;
    border-top: 1px solid #1c1c24;
  }
  .sub-title {
    font-size: 15px;
    font-weight: 700;
    color: #f4f4f5;
    margin-bottom: 6px;
  }
  .sub-desc {
    font-size: 13.5px;
    color: var(--muted);
    margin-bottom: 10px;
  }
  table.news-table {
    width: 100%;
    border-collapse: collapse;
    margin: 14px 0;
    font-size: 13px;
    border-radius: 8px;
    overflow: hidden;
    border: 1px solid var(--card-border);
  }
  table.news-table th {
    background: #181820;
    color: var(--text);
    padding: 10px 14px;
    text-align: left;
    font-weight: 600;
    border-bottom: 1px solid var(--card-border);
  }
  table.news-table td {
    padding: 9px 14px;
    color: #d4d4d8;
    border-bottom: 1px solid #1c1c24;
  }
  table.news-table tr:last-child td {
    border-bottom: none;
  }
  #lightbox {
    position: fixed;
    top: 0; left: 0; width: 100vw; height: 100vh;
    background: rgba(0, 0, 0, 0.93);
    backdrop-filter: blur(8px);
    display: none;
    align-items: center;
    justify-content: center;
    z-index: 9999;
    cursor: zoom-out;
  }
  #lightbox.show { display: flex; }
  #lightbox img {
    max-width: 95vw;
    max-height: 92vh;
    object-fit: contain;
    border-radius: 8px;
    box-shadow: 0 0 50px rgba(0,0,0,0.9);
  }
  .footer {
    text-align: center;
    font-size: 12px;
    color: var(--dim);
    margin-top: 40px;
  }
</style>
</head>
<body>
<div class="container">
  <div class="header-card">
''')

    if banner_url:
        html.append(f'    <div class="banner-wrap"><img src="{banner_url}" alt="Banner"></div>\n')

    html.append(f'''    <div class="header-content">
      <div class="meta-row">
        <span class="badge">{fmt(post.get('badge', 'Релиз'))}</span>
        <span class="date">{fmt(post.get('date', ''))}</span>
      </div>
      <h1>{fmt(post.get('title', 'Обновление'))}</h1>
      <p class="subtitle">{fmt(post.get('subtitle', ''))}</p>
    </div>
  </div>
''')

    def render_media_block(m):
        out = []
        if not m:
            return ''
        if isinstance(m, list):
            for sub_m in m:
                out.append(render_media_block(sub_m))
            return ''.join(out)
        if isinstance(m, dict):
            m_type = m.get('type', 'image')
            url = m.get('url', '')
            if m_type == 'youtube':
                yt_id = url.split('/')[-1]
                return f'<div class="media-plate" style="cursor:default;"><iframe src="https://www.youtube-nocookie.com/embed/{yt_id}" style="width:100%;aspect-ratio:16/9;border:none;display:block;" allowfullscreen></iframe></div>'
            b64 = embed_image(url)
            alt = m.get('alt', 'Изображение')
            return f'<div class="media-plate" onclick="zoomImage(this)"><img src="{b64}" alt="{alt}" loading="lazy"></div>'
        elif isinstance(m, str):
            b64 = embed_image(m)
            return f'<div class="media-plate" onclick="zoomImage(this)"><img src="{b64}" alt="Скриншот" loading="lazy"></div>'
        return ''

    for sec in post.get('sections', []):
        html.append('  <div class="section-card">\n')
        if sec.get('title'):
            html.append(f'    <div class="section-title">{fmt(sec["title"])}</div>\n')
        if sec.get('desc'):
            html.append(f'    <div class="section-desc">{fmt(sec["desc"])}</div>\n')
        if sec.get('items'):
            html.append('    <div class="items-list">\n')
            for it in sec['items']:
                html.append(f'      <div class="item-row">{fmt(it)}</div>\n')
            html.append('    </div>\n')
        if sec.get('media'):
            html.append(render_media_block(sec['media']))
        if sec.get('subsections'):
            for sub in sec['subsections']:
                html.append('    <div class="subsection">\n')
                if sub.get('title'):
                    html.append(f'      <div class="sub-title">{fmt(sub["title"])}</div>\n')
                if sub.get('desc'):
                    html.append(f'      <div class="sub-desc">{fmt(sub["desc"])}</div>\n')
                if sub.get('table'):
                    tbl = sub['table']
                    html.append('      <table class="news-table">\n        <thead><tr>')
                    for h in tbl.get('headers', []):
                        html.append(f'<th>{fmt(h)}</th>')
                    html.append('</tr></thead>\n        <tbody>')
                    for row in tbl.get('rows', []):
                        html.append('<tr>' + ''.join(f'<td>{fmt(c)}</td>' for c in row) + '</tr>')
                    html.append('</tbody>\n      </table>\n')
                if sub.get('items'):
                    html.append('      <div class="items-list">\n')
                    for it in sub['items']:
                        html.append(f'        <div class="item-row">{fmt(it)}</div>\n')
                    html.append('      </div>\n')
                if sub.get('media'):
                    html.append(render_media_block(sub['media']))
                html.append('    </div>\n')
        html.append('  </div>\n')

    html.append('''
  <div class="footer">Civilization Reborn • Автономный экспорт новостного поста</div>
</div>

<div id="lightbox" onclick="closeZoom()">
  <img id="lightbox-img" src="" alt="Zoomed">
</div>

<script>
function zoomImage(el) {
  const img = el.querySelector('img');
  if (!img) return;
  document.getElementById('lightbox-img').src = img.src;
  document.getElementById('lightbox').classList.add('show');
}
function closeZoom() {
  document.getElementById('lightbox').classList.remove('show');
}
document.addEventListener('keydown', function(e) {
  if (e.key === 'Escape') closeZoom();
});
</script>
</body>
</html>
''')

    out_path = os.path.join(root_dir, 'Civilization_Update_Post.html')
    with open(out_path, 'w', encoding='utf-8') as f:
        f.write(''.join(html))

    size_mb = os.path.getsize(out_path) / (1024 * 1024)
    print(f'SUCCESS: Created {out_path} ({size_mb:.2f} MB)')

if __name__ == '__main__':
    main()

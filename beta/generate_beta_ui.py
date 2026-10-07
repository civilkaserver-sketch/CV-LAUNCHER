import re

with open('ui.html', 'r', encoding='utf-8') as f:
    content = f.read().replace('\r\n', '\n')

# 1. Hide launcher update banner
content = content.replace(
    '<div id="launcherUpdateBanner" class="launcher-update-banner">',
    '<div id="launcherUpdateBanner" class="launcher-update-banner" style="display:none !important;">'
)
content = content.replace(
    '.launcher-update-banner{',
    '.launcher-update-banner{display:none !important;'
)
content = content.replace("url('assets/pool/pool_1.png')", "url('assets/bg.png')")



# 2. Update banner HTML
banner_repl = '''        <!-- БАННЕР -->
        <div class="card" id="bannerCard">
          <div class="banner-bg-layer" id="bannerBgA" style="opacity:1;background-image:url('assets/bg.png');"></div>
          <div class="banner-badge not-installed" id="bannerBadge"><span id="badgeText">Не установлено</span></div>

          <h1 class="banner-title">CIVILIZATION <span style="font-size:12px;color:var(--accent);vertical-align:middle;padding:2px 8px;border-radius:4px;background:rgba(var(--accent-rgb),0.12);font-weight:700;letter-spacing:1.5px;margin-left:6px;border:1px solid rgba(var(--accent-rgb),0.3);">BETA</span></h1>

          <div class="banner-sub">Бета-тест сборки · Minecraft 1.21.8 · Forge</div>'''

content = re.sub(
    r'<!-- БАННЕР -->\s*<div class="card" id="bannerCard">[\s\S]*?<div class="banner-sub">[\s\S]*?</div>',
    banner_repl,
    content
)
print('Banner HTML replaced')

# 3. Replace changelog card
old_cl = '''        <!-- БЛОК «ЧТО НОВОГО» (НА ВСЮ ШИРИНУ) -->
        <div class="card changelog-card">
          <div class="changelog-head">
            <h3>ЧТО НОВОГО</h3>
          </div>
          <div class="changelog-list-wrap">
            <div class="changelog-list" id="changelogList">
              <div class="changelog-item">
                <span class="bullet">—</span>
                <div class="cl-text"><strong>1. Главное меню (M)</strong>: быстрый доступ ко всем разделам без команд (Банк, Рынок, Хранилище, Трейды)</div>
              </div>
              <div class="changelog-item">
                <span class="bullet">—</span>
                <div class="cl-text"><strong>2. Безопасные Трейды</strong>: двусторонний обмен предметами с защитой от скама и сбросом подтверждения</div>
              </div>
              <div class="changelog-item">
                <span class="bullet">—</span>
                <div class="cl-text"><strong>3. Обновленная экономика</strong>: конвертация в Банке, ротация Рынка каждые 12ч и Хранилище до 162 слотов</div>
              </div>
              <div class="changelog-item">
                <span class="bullet">—</span>
                <div class="cl-text"><strong>4. Продажа предметов</strong>: выставление товаров на общий рынок игрокам и раздел «Мои лоты»</div>
              </div>
              <div class="changelog-item">
                <span class="bullet">—</span>
                <div class="cl-text"><strong>5. NPC и «Восьмёрка»</strong>: 8 секретных NPC по миру, уникальные события и коллекция артефактов</div>
              </div>
              <div class="changelog-item">
                <span class="bullet">—</span>
                <div class="cl-text"><strong>6. Таверна в Аду</strong>: Бармен с напитками и эффектами, сеть защищённых рельсов и вагонетки за $10</div>
              </div>
              <div class="changelog-item">
                <span class="bullet">—</span>
                <div class="cl-text"><strong>7. Союзы</strong>: система альянсов между игроками с полным отключением урона (без тимкилла)</div>
              </div>
            </div>
          </div>
          <div class="changelog-latest" id="changelogLatest" style="display:none">
            Вы на последней версии v1.21.8 · проверено сегодня
          </div>
          <button class="changelog-more-link" onclick="showNews(true)">Узнать больше</button>
        </div>'''

new_cl = '''        <!-- БЛОК «О ТЕСТИРОВАНИИ» -->
        <div class="card changelog-card">
          <div class="changelog-head">
            <h3>БЕТА-ТЕСТИРОВАНИЕ</h3>
          </div>
          <div class="changelog-list-wrap">
            <div class="changelog-list" id="changelogList">
              <div class="changelog-item">
                <span class="bullet">—</span>
                <div class="cl-text"><strong>Тестовая сборка</strong>: проверка стабильности клиента, совместимости модов и производительности.</div>
              </div>
              <div class="changelog-item">
                <span class="bullet">—</span>
                <div class="cl-text"><strong>Поиск ошибок</strong>: сообщайте обо всех найденных багах, вылетах и недочетах администрации сервера.</div>
              </div>
              <div class="changelog-item">
                <span class="bullet">—</span>
                <div class="cl-text"><strong>Связь</strong>: актуальная информация и обсуждение доступны в нашем сообществе.</div>
              </div>
            </div>
          </div>
        </div>'''

if old_cl in content:
    content = content.replace(old_cl, new_cl)
    print('Changelog replaced')
else:
    print('WARNING: old_cl not found')

# 4. News empty state
content = content.replace(
    '<div class="empty-title">НОВОСТИ ОТСУТСТВУЮТ</div>\n          <div class="empty-desc">Свежие события и обновления сборки появятся здесь автоматически</div>',
    '<div class="empty-title">НОВОСТЕЙ ПОКА НЕТ</div>\n          <div class="empty-desc">Следите за событиями и анонсами на нашем сервере</div>'
)

# 5. Clear DEFAULT_NEWS (lines 2976 to 3114)
content = re.sub(r'const DEFAULT_NEWS = \[[\s\S]*?\];\s*let currentNewsData', 'const DEFAULT_NEWS = [];\n  let currentNewsData', content)
print('DEFAULT_NEWS cleared')

# 6. BANNER_POOL and Slideshow scripts (lines 3616 to 3670)
content = re.sub(r'const BANNER_POOL = \[[\s\S]*?function nextBannerSlide\(\) \{[\s\S]*?\n  \}', 
'''const BANNER_POOL = ['assets/bg.png'];

  let bannerSlideTimer = null;
  let bannerPoolIdx = 0;
  let activeBannerLayer = 'A';

  function startBannerSlideshow() { }
  function stopBannerSlideshow() { }
  function nextBannerSlide() { }''', content)
print('BANNER_POOL and slideshow replaced')

# 7. resetBannerToDefault
old_reset = '''  function resetBannerToDefault() {
    const layerA = document.getElementById('bannerBgA');
    const layerB = document.getElementById('bannerBgB');
    if (layerA && layerB) {
      layerA.style.backgroundImage = 'url("' + BANNER_POOL[bannerPoolIdx] + '")';
      layerA.style.opacity = '1';
      layerB.style.opacity = '0';
      activeBannerLayer = 'A';
    }
    document.documentElement.style.setProperty('--banner-bg', 'url("' + BANNER_POOL[bannerPoolIdx] + '")');
    startBannerSlideshow();
  }'''

new_reset = '''  function resetBannerToDefault() {
    const layerA = document.getElementById('bannerBgA');
    const layerB = document.getElementById('bannerBgB');
    if (layerA) {
      layerA.style.backgroundImage = 'url("assets/bg.png")';
      layerA.style.opacity = '1';
    }
    if (layerB) {
      layerB.style.opacity = '0';
    }
    document.documentElement.style.setProperty('--banner-bg', 'url("assets/bg.png")');
  }'''

if old_reset in content:
    content = content.replace(old_reset, new_reset)
    print('resetBannerToDefault replaced')
else:
    print('WARNING: old_reset not found')

# 8. loadAndRenderNews -> always empty in beta
content = re.sub(r'async function loadAndRenderNews\(\) \{[\s\S]*?renderAllNews\(currentNewsData\);\s*\}',
'''async function loadAndRenderNews() {
    currentNewsData = [];
    renderAllNews([]);
  }''', content)
print('loadAndRenderNews replaced')

# 9. Settings modal launcher version badge
content = content.replace(
    '<span id="launcherVersionBadge" style="font-family:monospace;background:rgba(255,255,255,0.06);padding:2px 8px;border-radius:4px;font-size:12px;">v1.0.0</span>',
    '<span id="launcherVersionBadge" style="font-family:monospace;background:rgba(255,255,255,0.06);padding:2px 8px;border-radius:4px;font-size:12px;color:var(--accent);">BETA</span>'
)
content = content.replace(
    '<span id="launcherVersionStatus" style="font-size:12px;color:var(--muted);font-weight:normal;"></span>',
    '<span id="launcherVersionStatus" style="font-size:12px;color:var(--muted);font-weight:normal;">(тестовая сборка)</span>'
)

with open('beta/ui.html', 'w', encoding='utf-8') as f:
    f.write(content)
print('beta/ui.html generated successfully!')

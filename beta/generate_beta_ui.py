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

# 2. Completely remove banner from bannerCard (NO banner background, NO banner layers, NO min-height)
banner_css_override = '''
    /* БЕТА: Полное удаление баннера с карточки bannerCard */
    #bannerCard{
      min-height: auto !important;
      padding: 26px 28px !important;
      background: var(--card) !important;
      justify-content: flex-start !important;
    }
    #bannerCard::before,
    #bannerCard::after,
    #bannerCard .banner-bg-layer{
      display: none !important;
    }
'''
content = content.replace('/* карточки */', banner_css_override + '\n    /* карточки */')

# Update banner HTML without ANY banner-bg-layer
banner_repl = '''        <!-- КАРТОЧКА ЗАПУСКА (БЕЗ БАННЕРА ДЛЯ БЕТЫ) -->
        <div class="card" id="bannerCard">
          <div class="banner-badge not-installed" id="bannerBadge"><span id="badgeText">Не установлено</span></div>

          <h1 class="banner-title">CIVILIZATION <span style="font-size:12px;color:var(--accent);vertical-align:middle;padding:2px 8px;border-radius:4px;background:rgba(var(--accent-rgb),0.12);font-weight:700;letter-spacing:1.5px;margin-left:6px;border:1px solid rgba(var(--accent-rgb),0.3);">BETA</span></h1>

          <div class="banner-sub">Бета-тест сборки · Minecraft 1.21.8 · Fabric · 18 модов</div>'''

content = re.sub(
    r'<!-- БАННЕР -->\s*<div class="card" id="bannerCard">[\s\S]*?<div class="banner-sub">[\s\S]*?</div>',
    banner_repl,
    content
)
print('Banner HTML replaced (no banner layers)')

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

# 4. News empty state and news container
content = content.replace(
    '<div class="empty-state" id="newsEmpty" style="display:none">',
    '<div class="empty-state" id="newsEmpty" style="display:flex !important;">'
)
content = content.replace(
    '<div id="newsContent" class="news-list-wrap">',
    '<div id="newsContent" class="news-list-wrap" style="display:none !important;">'
)
content = content.replace(
    '<div class="empty-title">ОБНОВЛЕНИЯ ОТСУТСТВУЮТ</div>\n          <div class="empty-desc">Свежие события и обновления сборки появятся здесь автоматически</div>',
    '<div class="empty-title">ОБНОВЛЕНИЙ ПОКА НЕТ</div>\n          <div class="empty-desc">Следите за событиями и анонсами на нашем сервере</div>'
)

# 5. Clear DEFAULT_NEWS (lines 2976 to 3114)
content = re.sub(r'const DEFAULT_NEWS = \[[\s\S]*?\];\s*let currentNewsData', 'const DEFAULT_NEWS = [];\n  let currentNewsData', content)
print('DEFAULT_NEWS cleared')

# 6. BANNER_POOL and Slideshow scripts
content = re.sub(r'const BANNER_POOL = \[[\s\S]*?function nextBannerSlide\(\) \{[\s\S]*?\n  \}', 
'''const BANNER_POOL = [];
  let bannerSlideTimer = null;
  let bannerPoolIdx = 0;
  let activeBannerLayer = 'A';

  function startBannerSlideshow() { }
  function stopBannerSlideshow() { }
  function nextBannerSlide() { }''', content)
print('BANNER_POOL and slideshow removed')

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

new_reset = '''  function resetBannerToDefault() { }'''

if old_reset in content:
    content = content.replace(old_reset, new_reset)
    print('resetBannerToDefault replaced')
else:
    print('WARNING: old_reset not found')

# 8. renderAllNews & loadAndRenderNews -> strictly empty in beta
content = re.sub(
    r'function renderAllNews\(list\) \{[\s\S]*?async function loadAndRenderNews\(\) \{[\s\S]*?renderAllNews\(currentNewsData\);\s*\}',
    '''function renderAllNews(list) {
    const container = document.getElementById('newsContent');
    const emptyEl = document.getElementById('newsEmpty');
    if (container) { container.innerHTML = ''; container.style.display = 'none'; }
    if (emptyEl) { emptyEl.style.display = 'flex'; }
  }

  async function loadAndRenderNews() {
    currentNewsData = [];
    renderAllNews([]);
  }''',
    content
)
print('renderAllNews & loadAndRenderNews replaced with strict empty handlers')

# 9. Settings modal: launcher version badge
content = re.sub(
    r'<span id="launcherVersionBadge"[^>]*>.*?</span>',
    '<span id="launcherVersionBadge" style="font-family:monospace;background:rgba(255,255,255,0.06);padding:2px 8px;border-radius:4px;font-size:12px;color:var(--accent);">BETA</span>',
    content
)
content = re.sub(
    r'<span id="launcherVersionStatus"[^>]*>.*?</span>',
    '<span id="launcherVersionStatus" style="font-size:12px;color:var(--muted);font-weight:normal;">· (тестовая сборка)</span>',
    content
)

with open('beta/ui.html', 'w', encoding='utf-8') as f:
    f.write(content)
print('beta/ui.html generated successfully!')

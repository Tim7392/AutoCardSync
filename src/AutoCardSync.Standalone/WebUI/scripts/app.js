(() => {
  'use strict';

  const SCHEMA_VERSION = 1;
  const MAX_NATIVE_MESSAGE_CHARS = 131072;
  const METRIC_RENDER_INTERVAL_MS = 610;
  const REQUEST_TIMEOUT_MS = 30000;
  const MOTION_VALUE = Object.freeze({
    durationMs: 330,
    travelPercent: 24,
    midpointPercent: 6,
    maxBlurPx: 3.2,
    midpointBlurPx: 1,
    enterMidpoint: 0.56,
    exitMidpoint: 0.50,
    easing: 'cubic-bezier(.22,1,.36,1)'
  });
  const COMMAND_TYPES = new Set([
    'configuration.get',
    'configuration.save',
    'picker.selectFolder',
    'status.refresh',
    'task.retry',
    'task.restartFresh',
    'card.reassociate',
    'card.reinitialize',
    'card.profile.rename',
    'card.profile.configure',
    'card.profile.reinitialize',
    'card.confirmSourceCleanup',
    'task.cancel',
    'media.defer'
  ]);
  const TARGET_STATE_LABELS = Object.freeze({
    waiting: '等待开始',
    preparing: '正在准备',
    copying: '正在保存',
    verifying: '正在完整校验',
    complete: '保存并校验完成',
    paused: '已暂停',
    failed: '需要处理'
  });
  const PHASE_LABELS = Object.freeze({
    waiting: '等待插卡',
    preparing: '正在准备',
    copying: '正在保存',
    publishing: '正在原子发布最终文件',
    'verifying-temporary': '正在完整校验临时文件',
    'verifying-final': '正在从最终路径完整校验',
    verifying: '正在完整校验',
    recovering: '正在检查恢复状态',
    paused: '已暂停'
  });
  const COMMON_EXTENSIONS = new Set(['.jpg', '.jpeg', '.png', '.heic', '.mp4', '.mov', '.mxf', '.xml', '.xmp']);

  const screens = new Map([...document.querySelectorAll('.screen')].map(node => [node.id, node]));
  const byId = id => document.getElementById(id);
  const bridge = window.chrome && window.chrome.webview ? window.chrome.webview : null;
  const pendingRequests = new Map();
  const SENSITIVE_COMMAND_TYPES = new Set(['configuration.save', 'card.profile.configure']);
  let currentOperationId = null;
  let currentScreen = 'home';
  let latestStatus = null;
  let latestConfiguration = null;
  let latestMediaStatus = null;
  let cameraTemplates = [];
  let activeCameraTemplateId = '';
  let defaultCameraTemplateId = '';
  let selectedSourceDirectories = [];
  const customExtensions = new Set();
  let currentSetupStep = 0;
  let setupFirstRun = false;
  let pendingManagedRebind = null;
  const pendingMetricRenders = new Map();
  let metricRenderTimer = 0;
  let lastMetricRenderAt = Number.NEGATIVE_INFINITY;
  function isPlainObject(value) {
    return value !== null && typeof value === 'object' && !Array.isArray(value);
  }

  function clampPercent(value) {
    if (!Number.isFinite(value)) return 0;
    return Math.min(100, Math.max(0, value));
  }

  function safeText(value, fallback = '') {
    return typeof value === 'string' && value.trim() ? value.trim().slice(0, 600) : fallback;
  }

  function normalizeTargetMode(value) {
    if (!['nas-only', 'local-only', 'local-and-nas'].includes(value)) throw new Error('保存方式无效');
    return value;
  }

  function showScreen(name) {
    if (!screens.has(name)) name = 'failure';
    screens.forEach((screen, key) => {
      const active = key === name;
      screen.classList.toggle('active', active);
      screen.setAttribute('aria-hidden', active ? 'false' : 'true');
    });
    currentScreen = name;
    const focusTarget = screens.get(name).querySelector('h1,h2,button,[tabindex="0"]');
    if (focusTarget) focusTarget.focus({ preventScroll: true });
  }

  function showToast(message) {
    const toast = byId('toast');
    toast.textContent = safeText(message, '已更新').slice(0, 180);
    toast.classList.add('show');
    window.clearTimeout(showToast.timer);
    showToast.timer = window.setTimeout(() => toast.classList.remove('show'), 2200);
  }

  function newRequestId() {
    if (window.crypto && typeof window.crypto.randomUUID === 'function') return window.crypto.randomUUID();
    const bytes = new Uint8Array(16);
    window.crypto.getRandomValues(bytes);
    bytes[6] = (bytes[6] & 0x0f) | 0x40;
    bytes[8] = (bytes[8] & 0x3f) | 0x80;
    const hex = [...bytes].map(value => value.toString(16).padStart(2, '0')).join('');
    return `${hex.slice(0,8)}-${hex.slice(8,12)}-${hex.slice(12,16)}-${hex.slice(16,20)}-${hex.slice(20)}`;
  }

  function normalizeCameraTemplate(value, index, legacyDirectories = [], legacyExtensions = []) {
    const template = isPlainObject(value) ? value : {};
    const templateId = safeText(template.templateId) || newRequestId();
    const name = safeText(template.name, `相机模板 ${index + 1}`);
    return {
      templateId,
      name,
      approvedSourceDirectories: Array.isArray(template.approvedSourceDirectories)
        ? [...template.approvedSourceDirectories]
        : [...legacyDirectories],
      approvedExtensions: Array.isArray(template.approvedExtensions)
        ? [...template.approvedExtensions]
        : [...legacyExtensions]
    };
  }

  function currentCameraTemplate() {
    return cameraTemplates.find(template => template.templateId === activeCameraTemplateId) || null;
  }

  function readApprovedExtensions() {
    const extensions = [...document.querySelectorAll('[data-approved-extension]:checked')]
      .map(input => normalizeExtension(input.value));
    customExtensions.forEach(extension => extensions.push(extension));
    return [...new Set(extensions)];
  }

  function captureActiveCameraTemplate() {
    const template = currentCameraTemplate();
    if (!template) return;
    template.name = safeText(byId('cameraTemplateName').value, template.name);
    template.approvedSourceDirectories = [...selectedSourceDirectories];
    template.approvedExtensions = readApprovedExtensions();
  }

  function renderCameraTemplateControls() {
    const select = byId('cameraTemplateSelect');
    select.replaceChildren();
    cameraTemplates.forEach(template => {
      const option = document.createElement('option');
      option.value = template.templateId;
      option.textContent = template.templateId === defaultCameraTemplateId
        ? `${template.name}（默认）`
        : template.name;
      select.appendChild(option);
    });
    select.value = activeCameraTemplateId;
    const isDefault = activeCameraTemplateId === defaultCameraTemplateId;
    const status = byId('defaultCameraTemplateStatus');
    status.textContent = isDefault ? '默认模板' : '非默认模板';
    status.classList.toggle('is-default', isDefault);
    byId('setDefaultCameraTemplateBtn').disabled = isDefault;
    const profiles = latestConfiguration && Array.isArray(latestConfiguration.cardProfiles)
      ? latestConfiguration.cardProfiles
      : [];
    const inUse = profiles.some(profile => isPlainObject(profile) && profile.cameraTemplateId === activeCameraTemplateId);
    byId('removeCameraTemplateBtn').disabled = cameraTemplates.length <= 1 || inUse;
    byId('removeCameraTemplateBtn').title = inUse ? '已有素材卡使用此模板，不能删除' : '';
  }

  function loadActiveCameraTemplate() {
    const template = currentCameraTemplate();
    if (!template) throw new Error('相机模板状态无效。');
    byId('cameraTemplateName').value = template.name;
    byId('activeCameraTemplateLabel').textContent = template.name;
    setSourceDirectories(template.approvedSourceDirectories);
    setApprovedExtensions(template.approvedExtensions);
    renderCameraTemplateControls();
  }

  function setCameraTemplates(configuration) {
    const legacyDirectories = Array.isArray(configuration.approvedSourceDirectories)
      ? configuration.approvedSourceDirectories
      : [];
    const legacyExtensions = Array.isArray(configuration.approvedExtensions)
      ? configuration.approvedExtensions
      : [];
    const supplied = Array.isArray(configuration.cameraTemplates) && configuration.cameraTemplates.length > 0
      ? configuration.cameraTemplates
      : [{
          templateId: safeText(configuration.defaultCameraTemplateId) || newRequestId(),
          name: '默认相机',
          approvedSourceDirectories: legacyDirectories,
          approvedExtensions: legacyExtensions
        }];
    cameraTemplates = supplied.map((value, index) =>
      normalizeCameraTemplate(value, index, legacyDirectories, legacyExtensions));
    const requestedDefault = safeText(configuration.defaultCameraTemplateId);
    defaultCameraTemplateId = cameraTemplates.some(template => template.templateId === requestedDefault)
      ? requestedDefault
      : cameraTemplates[0].templateId;
    activeCameraTemplateId = defaultCameraTemplateId;
    loadActiveCameraTemplate();
  }

  function switchCameraTemplate(templateId) {
    if (templateId === activeCameraTemplateId) return;
    captureActiveCameraTemplate();
    if (!cameraTemplates.some(template => template.templateId === templateId))
      throw new Error('选择的相机模板不存在。');
    activeCameraTemplateId = templateId;
    loadActiveCameraTemplate();
  }

  function addCameraTemplate() {
    captureActiveCameraTemplate();
    const source = currentCameraTemplate();
    const wasEmpty = cameraTemplates.length === 0;
    const template = {
      templateId: newRequestId(),
      name: `相机模板 ${cameraTemplates.length + 1}`,
      approvedSourceDirectories: [],
      approvedExtensions: source && source.approvedExtensions.length > 0
        ? [...source.approvedExtensions]
        : ['.jpg', '.jpeg', '.mp4', '.mov']
    };
    cameraTemplates.push(template);
    if (wasEmpty || !defaultCameraTemplateId) defaultCameraTemplateId = template.templateId;
    activeCameraTemplateId = template.templateId;
    loadActiveCameraTemplate();
    byId('cameraTemplateName').focus();
    byId('cameraTemplateName').select();
  }

  function removeActiveCameraTemplate() {
    if (cameraTemplates.length <= 1 || byId('removeCameraTemplateBtn').disabled) return;
    const removedId = activeCameraTemplateId;
    cameraTemplates = cameraTemplates.filter(template => template.templateId !== removedId);
    if (defaultCameraTemplateId === removedId) defaultCameraTemplateId = cameraTemplates[0].templateId;
    activeCameraTemplateId = defaultCameraTemplateId;
    loadActiveCameraTemplate();
  }

  function hasPendingSensitiveOperation() {
    return [...pendingRequests.values()].some(pending => SENSITIVE_COMMAND_TYPES.has(pending.type));
  }

  function expireRequest(requestId) {
    const pending = pendingRequests.get(requestId);
    if (!pending) return;
    if (SENSITIVE_COMMAND_TYPES.has(pending.type)) {
      pending.timedOut = true;
      pending.timeoutId = 0;
      byId('setupSubmitBtn').disabled = true;
      byId('setupError').textContent =
        '桌面应用仍可能正在保存设置，请勿重复提交。请保持应用打开并等待最终结果；如果长时间没有结果，请重启应用后先读取当前设置。';
      return;
    }
    pendingRequests.delete(requestId);
    showToast('桌面应用 30 秒内未响应，操作结果未确认；请先刷新状态再决定是否重试');
  }

  function postCommand(type, payload = {}, context = null) {
    if (SENSITIVE_COMMAND_TYPES.has(type) && hasPendingSensitiveOperation()) {
      byId('setupError').textContent = '上一项设置操作仍在处理中，请等待最终结果，不能重复提交。';
      return null;
    }
    if (!bridge) {
      if (type === 'configuration.save') byId('setupError').textContent = '桌面应用连接不可用，设置没有保存。';
      else if (!type.startsWith('window.')) showToast('桌面应用连接不可用；没有执行操作');
      return null;
    }
    const requestId = newRequestId();
    const requestKey = requestId.toLowerCase();
    const message = { schemaVersion: SCHEMA_VERSION, type, requestId, payload };
    if (COMMAND_TYPES.has(type)) {
      const timeoutId = window.setTimeout(() => expireRequest(requestKey), REQUEST_TIMEOUT_MS);
      pendingRequests.set(requestKey, { type, context, timeoutId });
    }
    bridge.postMessage(message);
    return requestId;
  }

  function openOverlay(id, focusId) {
    const overlay = byId(id);
    overlay.classList.add('open');
    overlay.setAttribute('aria-hidden', 'false');
    if (focusId) window.setTimeout(() => byId(focusId).focus(), 0);
  }

  function closeOverlay(id) {
    const overlay = byId(id);
    overlay.classList.remove('open');
    overlay.setAttribute('aria-hidden', 'true');
  }

  function openSetup(options = {}) {
    if (options.preserveManagedRebind !== true) pendingManagedRebind = null;
    setupFirstRun = options.firstRun === true || !(latestConfiguration && latestConfiguration.configured === true);
    byId('app').classList.toggle('first-run', setupFirstRun);
    byId('setupError').textContent = '';
    byId('setupSubmitBtn').disabled = hasPendingSensitiveOperation();
    showSetupStep(Number.isInteger(options.step) ? options.step : 0, false);
    showScreen('setup');
  }

  function closeSetup() {
    byId('setupSubmitBtn').disabled = hasPendingSensitiveOperation();
    if (setupFirstRun) return;
    pendingManagedRebind = null;
    if (latestConfiguration) {
      setCameraTemplates(latestConfiguration);
      renderManagedCards(latestConfiguration);
    }
    byId('app').classList.remove('first-run');
    showScreen('home');
  }

  function selectedTargetMode() {
    const selected = document.querySelector('input[name="targetMode"]:checked');
    const value = selected && selected.value;
    if (!['nas-only', 'local-only', 'local-and-nas'].includes(value)) throw new Error('请选择保存方式。');
    return value;
  }

  function updateTargetModeUi() {
    const targetMode = selectedTargetMode();
    const requiresLocal = targetMode !== 'nas-only';
    const requiresNas = targetMode !== 'local-only';
    const localPanel = document.querySelector('[data-target-panel="local"]');
    const nasPanel = document.querySelector('[data-target-panel="nas"]');
    localPanel.classList.toggle('target-disabled', !requiresLocal);
    nasPanel.classList.toggle('target-disabled', !requiresNas);
    byId('localTarget').disabled = !requiresLocal;
    byId('nasMappedTarget').disabled = !requiresNas;
    localPanel.querySelector('button').disabled = !requiresLocal;
    nasPanel.querySelector('button').disabled = !requiresNas;
  }

  function validateTargetSettings() {
    const targetMode = selectedTargetMode();
    const requiresLocal = targetMode !== 'nas-only';
    const requiresNas = targetMode !== 'local-only';
    const localTarget = requiresLocal ? normalizeWindowsPath(byId('localTarget').value) : '';
    const nasMappedTarget = requiresNas ? normalizeWindowsPath(byId('nasMappedTarget').value) : '';
    if (requiresLocal && !isDrivePath(localTarget)) throw new Error('请选择这台电脑上的本地保存文件夹。');
    if (requiresNas && !isDrivePath(nasMappedTarget)) throw new Error('请选择 Windows 已连接的 NAS 映射盘文件夹。');
    if (requiresLocal && requiresNas && pathsOverlap(localTarget, nasMappedTarget)) throw new Error('两个保存位置必须是真实不同的位置。');
    return { targetMode, localTarget, nasMappedTarget };
  }

  function validateSetupStep(step) {
    if (step === 0) collectSourceDirectories();
    else if (step === 1) collectApprovedExtensions();
    else if (step === 2) validateTargetSettings();
    else collectConfiguration();
  }

  function renderSetupSummary() {
    const configuration = collectConfiguration();
    const templates = configuration.cameraTemplates;
    const defaultTemplate = templates.find(template => template.templateId === configuration.defaultCameraTemplateId);
    const sources = defaultTemplate.approvedSourceDirectories;
    const extensions = defaultTemplate.approvedExtensions;
    const targets = configuration;
    const namingLabels = {
      'capture-date': '拍摄日期 / 保持原目录',
      'import-date': '导入日期 / 保持原目录',
      preserve: '直接保持素材卡目录'
    };
    byId('summaryCameraTemplates').textContent = `${templates.length} 个 · 默认：${defaultTemplate.name}`;
    byId('summarySourceCount').textContent = `${sources.length} 个 · ${sources.join('、')}`;
    byId('summaryExtensions').textContent = `${extensions.length} 种 · ${extensions.join('、')}`;
    const modeLabels = {
      'nas-only': '只保存到 NAS',
      'local-only': '只保存到本机',
      'local-and-nas': '本机 + NAS 双副本'
    };
    byId('summaryTargetMode').textContent = modeLabels[targets.targetMode];
    byId('summaryLocalTarget').textContent = targets.localTarget || '未启用';
    byId('summaryNasTarget').textContent = targets.nasMappedTarget || '未启用';
    byId('summaryNamingRule').textContent = namingLabels[byId('targetNamingRule').value] || '未选择';
    byId('summaryAutoStart').textContent = byId('autoStartOnLogin').checked ? '已开启' : '未开启';
  }

  function showSetupStep(step, validateCurrent = true) {
    const error = byId('setupError');
    error.textContent = '';
    if (validateCurrent) {
      try { validateSetupStep(currentSetupStep); }
      catch (exception) {
        error.textContent = exception instanceof Error ? exception.message : '请先完成当前步骤。';
        return false;
      }
    }

    if (currentSetupStep === 0 || currentSetupStep === 1) captureActiveCameraTemplate();
    currentSetupStep = Math.max(0, Math.min(3, step));
    document.querySelectorAll('[data-setup-step]').forEach(panel => {
      const active = Number(panel.dataset.setupStep) === currentSetupStep;
      panel.classList.toggle('active', active);
      panel.setAttribute('aria-hidden', active ? 'false' : 'true');
    });
    document.querySelectorAll('[data-setup-progress]').forEach(item => {
      const index = Number(item.dataset.setupProgress);
      item.classList.toggle('active', index === currentSetupStep);
      item.classList.toggle('complete', index < currentSetupStep);
    });
    byId('setupBackBtn').style.display = currentSetupStep > 0 ? 'inline-flex' : 'none';
    byId('setupNextBtn').style.display = currentSetupStep < 3 ? 'inline-flex' : 'none';
    byId('setupSubmitBtn').style.display = currentSetupStep === 3 ? 'inline-flex' : 'none';
    if (currentSetupStep === 3) renderSetupSummary();
    const activePanel = document.querySelector(`[data-setup-step="${currentSetupStep}"]`);
    const focusTarget = activePanel && activePanel.querySelector('button,input,select,h2');
    if (focusTarget) window.setTimeout(() => focusTarget.focus({ preventScroll: true }), 0);
    return true;
  }

  function normalizeSourceDirectory(value) {
    const directory = safeText(value).replace(/[\\/]+$/, '');
    if (!directory) throw new Error('请至少选择一个素材卡内的源目录。');
    if (directory.length > 240) throw new Error('批准源目录名称过长。');
    if (/^[a-z]:/i.test(directory) || directory.startsWith('\\') || directory.startsWith('/')) throw new Error('批准源目录必须来自素材卡文件夹选择器。');
    if (directory.split(/[\\/]+/).some(part => part === '..' || part === '.')) throw new Error('批准源目录不能包含返回上级目录的写法。');
    return directory;
  }

  function normalizeSourceDirectories(values) {
    const normalized = [];
    for (const value of Array.isArray(values) ? values : []) {
      const directory = normalizeSourceDirectory(value);
      const key = directory.toLocaleLowerCase('zh-CN');
      if (normalized.some(existing => {
        const parent = existing.toLocaleLowerCase('zh-CN');
        return key === parent || key.startsWith(`${parent}\\`);
      })) continue;
      for (let index = normalized.length - 1; index >= 0; index -= 1) {
        const existing = normalized[index].toLocaleLowerCase('zh-CN');
        if (existing.startsWith(`${key}\\`)) normalized.splice(index, 1);
      }
      normalized.push(directory);
    }
    return normalized;
  }

  function renderSourceDirectories() {
    const list = byId('approvedSourceDirectoryList');
    list.replaceChildren();
    if (selectedSourceDirectories.length === 0) {
      const empty = document.createElement('p');
      empty.className = 'selection-empty';
      empty.textContent = '尚未选择素材卡内的文件夹';
      list.appendChild(empty);
      return;
    }
    selectedSourceDirectories.forEach(directory => {
      const row = document.createElement('div');
      row.className = 'source-folder-item';
      const path = document.createElement('span');
      path.textContent = `素材卡内 \\${directory}`;
      const remove = document.createElement('button');
      remove.type = 'button';
      remove.className = 'selection-remove';
      remove.textContent = '移除';
      remove.addEventListener('click', () => {
        selectedSourceDirectories = selectedSourceDirectories.filter(item => item.toLocaleLowerCase('zh-CN') !== directory.toLocaleLowerCase('zh-CN'));
        renderSourceDirectories();
      });
      row.append(path, remove);
      list.appendChild(row);
    });
  }

  function setSourceDirectories(values) {
    selectedSourceDirectories = normalizeSourceDirectories(values);
    renderSourceDirectories();
  }

  function addSourceDirectory(value) {
    const directory = normalizeSourceDirectory(value);
    if (selectedSourceDirectories.length >= 32) throw new Error('批准源目录不能超过 32 个。');
    selectedSourceDirectories = normalizeSourceDirectories([...selectedSourceDirectories, directory]);
    renderSourceDirectories();
  }

  function collectSourceDirectories() {
    if (selectedSourceDirectories.length === 0) throw new Error('请从素材卡选择至少一个源目录。');
    return normalizeSourceDirectories(selectedSourceDirectories);
  }

  function normalizeExtension(value) {
    const trimmed = safeText(value).toLowerCase();
    const extension = trimmed && !trimmed.startsWith('.') ? `.${trimmed}` : trimmed;
    if (!/^\.[a-z0-9]{1,12}$/.test(extension)) throw new Error('扩展名必须使用“.jpg”这样的格式。');
    return extension;
  }

  function renderCustomExtensions() {
    const list = byId('customExtensionList');
    list.replaceChildren();
    customExtensions.forEach(extension => {
      const chip = document.createElement('span');
      chip.className = 'custom-extension-chip';
      chip.textContent = extension;
      const remove = document.createElement('button');
      remove.type = 'button';
      remove.setAttribute('aria-label', `移除 ${extension}`);
      remove.textContent = '×';
      remove.addEventListener('click', () => { customExtensions.delete(extension); renderCustomExtensions(); });
      chip.appendChild(remove);
      list.appendChild(chip);
    });
  }

  function setApprovedExtensions(values) {
    const commonInputs = [...document.querySelectorAll('[data-approved-extension]')];
    commonInputs.forEach(input => { input.checked = false; });
    customExtensions.clear();
    for (const value of Array.isArray(values) ? values : []) {
      const extension = normalizeExtension(value);
      const common = commonInputs.find(input => input.value.toLowerCase() === extension);
      if (common) common.checked = true;
      else customExtensions.add(extension);
    }
    renderCustomExtensions();
  }

  function collectApprovedExtensions() {
    const unique = readApprovedExtensions();
    if (unique.length === 0) throw new Error('请至少勾选或增加一种批准扩展名。');
    if (unique.length > 64) throw new Error('批准扩展名不能超过 64 种。');
    return unique;
  }

  function addCustomExtension() {
    const input = byId('customExtensionInput');
    const error = byId('setupError');
    error.textContent = '';
    try {
      const extension = normalizeExtension(input.value);
      const common = [...document.querySelectorAll('[data-approved-extension]')]
        .find(item => item.value.toLowerCase() === extension);
      if (common) common.checked = true;
      else {
        const selectedCount = document.querySelectorAll('[data-approved-extension]:checked').length + customExtensions.size;
        if (!customExtensions.has(extension) && selectedCount >= 64) throw new Error('批准扩展名不能超过 64 种。');
        customExtensions.add(extension);
        renderCustomExtensions();
      }
      input.value = '';
      input.focus();
    } catch (exception) {
      error.textContent = exception instanceof Error ? exception.message : '扩展名无效。';
    }
  }

  function normalizeWindowsPath(value) {
    const normalized = value.trim().replace(/\//g, '\\');
    return /^[a-z]:\\$/i.test(normalized) ? normalized : normalized.replace(/[\\]+$/, '');
  }

  function isDrivePath(value) {
    return /^[a-z]:\\[^\\]/i.test(value) || /^[a-z]:\\$/i.test(value);
  }

  function pathsOverlap(first, second) {
    const a = first.toLocaleLowerCase('zh-CN');
    const b = second.toLocaleLowerCase('zh-CN');
    return a === b || a.startsWith(`${b}\\`) || b.startsWith(`${a}\\`);
  }

  function collectConfiguration() {
    captureActiveCameraTemplate();
    if (cameraTemplates.length === 0) throw new Error('请至少保留一个相机模板。');
    const names = new Set();
    const normalizedTemplates = cameraTemplates.map((template, index) => {
      const name = safeText(template.name);
      if (!name) throw new Error(`相机模板 ${index + 1} 需要名称。`);
      const nameKey = name.toLocaleLowerCase('zh-CN');
      if (names.has(nameKey)) throw new Error('相机模板名称不能重复。');
      names.add(nameKey);
      const directories = normalizeSourceDirectories(template.approvedSourceDirectories);
      if (directories.length === 0) throw new Error(`请为“${name}”选择至少一个素材目录。`);
      const extensions = [...new Set(template.approvedExtensions.map(normalizeExtension))];
      if (extensions.length === 0) throw new Error(`请为“${name}”选择至少一种文件类型。`);
      return {
        templateId: template.templateId,
        name,
        approvedSourceDirectories: directories,
        approvedExtensions: extensions
      };
    });
    const defaultTemplate = normalizedTemplates.find(template => template.templateId === defaultCameraTemplateId);
    if (!defaultTemplate) throw new Error('请选择有效的默认相机模板。');
    const { targetMode, localTarget, nasMappedTarget } = validateTargetSettings();
    const targetNamingRule = byId('targetNamingRule').value;
    const autoStartOnLogin = byId('autoStartOnLogin').checked;
    if (!['capture-date', 'import-date', 'preserve'].includes(targetNamingRule)) throw new Error('目标命名规则无效。');
    const cardProfiles = latestConfiguration && Array.isArray(latestConfiguration.cardProfiles)
      ? latestConfiguration.cardProfiles
      : [];
    return {
      approvedSourceDirectories: defaultTemplate.approvedSourceDirectories,
      approvedExtensions: defaultTemplate.approvedExtensions,
      defaultCameraTemplateId,
      cameraTemplates: normalizedTemplates,
      cardProfiles,
      targetMode,
      localTarget,
      nasMappedTarget,
      targetNamingRule,
      autoStartOnLogin
    };
  }

  function cardTextList(value) {
    return Array.isArray(value)
      ? value.map(item => safeText(item)).filter(Boolean).slice(0, 32)
      : [];
  }

  function managedCardHealthLabel(value) {
    const labels = {
      healthy: '已就绪',
      initialization_pending: '初始化尚未完成',
      legacy_profile_missing: '历史档案待恢复',
      identity_only: '身份记录待恢复',
      baseline_missing: '基线记录待恢复',
      identity_binding_missing: '身份绑定待恢复',
      profile_missing: '档案待恢复',
      scope_missing: '素材范围待恢复'
    };
    return labels[safeText(value)] || '已认识的素材卡';
  }

  function knownCardsForConfiguration(configuration) {
    if (!configuration) return [];
    const templates = Array.isArray(configuration.cameraTemplates) ? configuration.cameraTemplates : [];
    const profiles = Array.isArray(configuration.cardProfiles) ? configuration.cardProfiles : [];
    if (Array.isArray(configuration.knownCards)) {
      return configuration.knownCards.map((value, index) => {
        const card = isPlainObject(value) ? value : {};
        const cardInstanceId = safeText(card.cardInstanceId);
        const profile = profiles.find(item => safeText(item.cardInstanceId) === cardInstanceId) || null;
        const cameraTemplateId = safeText(card.cameraTemplateId, profile ? safeText(profile.cameraTemplateId) : '');
        const template = templates.find(item => safeText(item.templateId) === cameraTemplateId) || null;
        return {
          cardInstanceId,
          displayName: safeText(card.displayName, profile ? safeText(profile.displayName) : `素材卡 ${index + 1}`),
          cameraTemplateId,
          cameraTemplateName: safeText(card.cameraTemplateName, template ? safeText(template.name) : ''),
          approvedSourceDirectories: cardTextList(card.approvedSourceDirectories),
          approvedExtensions: cardTextList(card.approvedExtensions),
          hasProfile: typeof card.hasProfile === 'boolean' ? card.hasProfile : profile !== null,
          hasIdentityBinding: card.hasIdentityBinding === true,
          hasBaseline: card.hasBaseline === true,
          initializationPending: card.initializationPending === true,
          healthState: safeText(card.healthState, 'healthy'),
          profile,
          template
        };
      });
    }
    return profiles.map((profile, index) => {
      const cardInstanceId = safeText(profile.cardInstanceId);
      const cameraTemplateId = safeText(profile.cameraTemplateId);
      const template = templates.find(item => safeText(item.templateId) === cameraTemplateId) || null;
      return {
        cardInstanceId,
        displayName: safeText(profile.displayName, `素材卡 ${index + 1}`),
        cameraTemplateId,
        cameraTemplateName: template ? safeText(template.name) : '',
        approvedSourceDirectories: template ? cardTextList(template.approvedSourceDirectories) : [],
        approvedExtensions: template ? cardTextList(template.approvedExtensions) : [],
        hasProfile: true,
        hasIdentityBinding: true,
        hasBaseline: true,
        initializationPending: false,
        healthState: template ? 'healthy' : 'scope_missing',
        profile,
        template
      };
    });
  }

  function scopeTemplateForManagedCard(card, displayName) {
    const existing = cameraTemplates.find(template => template.templateId === card.cameraTemplateId);
    if (existing) return existing;
    const directories = cardTextList(card.approvedSourceDirectories);
    const extensions = cardTextList(card.approvedExtensions);
    if (directories.length > 0 && extensions.length > 0) {
      return {
        templateId: newRequestId(),
        name: safeText(card.cameraTemplateName, `${displayName} 原范围`),
        approvedSourceDirectories: directories,
        approvedExtensions: extensions
      };
    }
    // Legacy cards can have identity/baseline evidence without a surviving
    // profile. Use the current default only as an editable starting point; the
    // explicit card operation below still creates a separate template and
    // rebinds only the selected, currently mounted CardId.
    return cameraTemplates.find(template => template.templateId === defaultCameraTemplateId) ||
      currentCameraTemplate();
  }

  function beginManagedCardScopeEdit(card, displayName) {
    captureActiveCameraTemplate();
    const sourceTemplate = scopeTemplateForManagedCard(card, displayName);
    if (!sourceTemplate) {
      showToast('这张卡的原素材范围不完整，插入该卡后请从首页的“需要你的决定”继续恢复。');
      return;
    }
    const suffix = '新范围';
    let candidateName = `${displayName} ${suffix}`;
    let sequence = 2;
    const existingNames = new Set(cameraTemplates.map(item => safeText(item.name).toLocaleLowerCase('zh-CN')));
    while (existingNames.has(candidateName.toLocaleLowerCase('zh-CN'))) {
      candidateName = `${displayName} ${suffix} ${sequence}`;
      sequence += 1;
    }
    const clone = {
      templateId: newRequestId(),
      name: candidateName,
      approvedSourceDirectories: [...sourceTemplate.approvedSourceDirectories],
      approvedExtensions: [...sourceTemplate.approvedExtensions]
    };
    cameraTemplates.push(clone);
    activeCameraTemplateId = clone.templateId;
    pendingManagedRebind = {
      cardInstanceId: card.cardInstanceId,
      cameraTemplateId: clone.templateId,
      displayName
    };
    loadActiveCameraTemplate();
    renderManagedCards();
    openSetup({ preserveManagedRebind: true, step: 0 });
    byId('setupError').textContent =
      `正在为“${displayName}”建立独立的新素材范围。旧模板和历史记录不会被覆盖；完成设置前请插入并保持这张卡连接。`;
  }

  function renderManagedCards(configuration = latestConfiguration) {
    const list = byId('managedCardList');
    list.replaceChildren();
    const cards = knownCardsForConfiguration(configuration);
    if (cards.length === 0) {
      const empty = document.createElement('p');
      empty.className = 'selection-empty managed-card-empty';
      empty.textContent = '尚无已认识的素材卡。插入第一张卡后，系统会先说明它是新卡、旧卡还是需要恢复。';
      list.appendChild(empty);
      return;
    }

    cards.forEach((card, index) => {
      const cardId = safeText(card.cardInstanceId);
      const displayName = safeText(card.displayName, cardId ? `素材卡 ${cardId.slice(0, 8)}` : `素材卡 ${index + 1}`);
      const directories = cardTextList(card.approvedSourceDirectories);
      const extensions = cardTextList(card.approvedExtensions);
      const row = document.createElement('article');
      row.className = 'managed-card-item';
      if (safeText(card.healthState) === 'legacy_profile_missing') row.classList.add('needs-recovery');

      const copy = document.createElement('div');
      copy.className = 'managed-card-copy';
      const title = document.createElement('div');
      title.className = 'managed-card-title';
      const name = document.createElement('strong');
      name.textContent = displayName;
      const id = document.createElement('span');
      id.textContent = cardId ? `CardId ${cardId.slice(0, 8)}` : '历史记录';
      title.append(name, id);
      const templateName = document.createElement('p');
      templateName.textContent = card.hasProfile
        ? `素材范围：${safeText(card.cameraTemplateName, '未命名模板')}`
        : `${managedCardHealthLabel(card.healthState)}：保留了历史识别与任务证据，不会被静默丢弃`;
      const scope = document.createElement('small');
      scope.textContent = directories.length > 0 || extensions.length > 0
        ? `${directories.length > 0 ? directories.join('、') : '未记录目录'} · ${extensions.length > 0 ? extensions.join('、') : '未记录类型'}`
        : '尚未恢复可用的素材范围；插入此卡后可在首页查看下一步。';
      copy.append(title, templateName, scope);

      const actions = document.createElement('div');
      actions.className = 'managed-card-actions';
      if (card.hasProfile && cardId) {
        const rename = document.createElement('button');
        rename.type = 'button';
        rename.className = 'btn secondary compact';
        rename.textContent = '重命名';
        rename.disabled = pendingManagedRebind !== null;
        rename.addEventListener('click', () => {
          const nextName = window.prompt('输入这张素材卡在本机显示的名称：', displayName);
          if (nextName === null) return;
          const normalized = nextName.trim();
          if (!normalized || normalized.length > 64) {
            showToast('素材卡名称必须为 1 到 64 个字符');
            return;
          }
          postCommand('card.profile.rename', { cardInstanceId: cardId, displayName: normalized });
        });
        const reconfigure = document.createElement('button');
        reconfigure.type = 'button';
        reconfigure.className = 'btn secondary compact';
        reconfigure.textContent = '调整此卡素材范围';
        reconfigure.disabled = pendingManagedRebind !== null;
        reconfigure.addEventListener('click', () => beginManagedCardScopeEdit(card, displayName));
        actions.append(rename, reconfigure);
      } else if (cardId) {
        const recovery = document.createElement('button');
        recovery.type = 'button';
        recovery.className = 'btn secondary compact';
        recovery.textContent = '恢复档案并调整范围';
        recovery.disabled = pendingManagedRebind !== null;
        recovery.addEventListener('click', () => beginManagedCardScopeEdit(card, displayName));
        actions.appendChild(recovery);
      } else {
        const recovery = document.createElement('span');
        recovery.className = 'managed-card-recovery-note';
        recovery.textContent = '插入此卡后，首页会给出恢复或重新绑定的下一步。';
        actions.appendChild(recovery);
      }
      row.append(copy, actions);
      list.appendChild(row);
    });
  }

  function submitSetup(event) {
    event.preventDefault();
    if (currentSetupStep < 3) {
      showSetupStep(currentSetupStep + 1, true);
      return;
    }
    const error = byId('setupError');
    error.textContent = '';
    if (hasPendingSensitiveOperation()) {
      error.textContent = '上一项设置操作仍在处理中，请等待最终结果，不能重复提交。';
      return;
    }
    try {
      const configuration = collectConfiguration();
      let type = 'configuration.save';
      let payload = configuration;
      if (pendingManagedRebind) {
        const confirmed = window.confirm(
          `请确认当前插入并保持连接的介质就是“${pendingManagedRebind.displayName}”。` +
          '系统会保留旧任务、回执和清理前基线快照，再把这张卡绑定到新素材范围。' +
          '当前符合新范围的文件会重新检查，必要时重新复制，因此可能产生重复副本。确定继续吗？');
        if (!confirmed) return;
        type = 'card.profile.configure';
        payload = {
          configuration,
          cardInstanceId: pendingManagedRebind.cardInstanceId,
          cameraTemplateId: pendingManagedRebind.cameraTemplateId,
          confirmed: true
        };
      }
      byId('setupSubmitBtn').disabled = true;
      const requestId = postCommand(type, payload, { configuration });
      if (!requestId) byId('setupSubmitBtn').disabled = hasPendingSensitiveOperation();
    } catch (exception) {
      error.textContent = exception instanceof Error ? exception.message : '设置内容无效。';
    }
  }

  function renderConfiguration(configuration, options = {}) {
    if (!isPlainObject(configuration)) throw new Error('设置状态格式无效');
    const configured = configuration.configured === true;
    const wasAutomaticFirstRun = setupFirstRun;
    const preserveSetupDraft =
      currentScreen === 'setup' &&
      latestConfiguration !== null &&
      options.forceFormReset !== true;
    latestConfiguration = configuration;
    byId('configurationDot').classList.toggle('offline', !configured);
    byId('configurationLabel').textContent = configured ? '自动同步已配置' : '等待完成首次配置';
    byId('configurationSummary').textContent = configured ? '已准备好自动同步' : '尚未完成首次配置';
    byId('configurationHint').textContent = configured
      ? `${Array.isArray(configuration.knownCards) ? configuration.knownCards.length : Array.isArray(configuration.cardProfiles) ? configuration.cardProfiles.length : 0} 张已认识的素材卡 · ${Array.isArray(configuration.cameraTemplates) && configuration.cameraTemplates.length > 0 ? configuration.cameraTemplates.length : 1} 个素材范围`
      : '设置素材目录、文件类型和保存目标后即可自动运行。';
    byId('openSetupBtn').textContent = configured ? '查看与修改设置' : '完成首次配置';
    byId('manageCardsBtn').hidden = !configured;
    byId('failureManageCardsBtn').hidden = !configured;

    // Runtime/status pushes may arrive while the user is editing. They update the
    // authoritative snapshot, but must not replace the unsaved form or the
    // temporary template used by an explicit per-card scope change.
    if (!preserveSetupDraft) {
      setCameraTemplates(configuration);
      renderManagedCards(configuration);
      if (['nas-only', 'local-only', 'local-and-nas'].includes(configuration.targetMode)) {
        const mode = document.querySelector(`input[name="targetMode"][value="${configuration.targetMode}"]`);
        if (mode) mode.checked = true;
      }
      updateTargetModeUi();
      if (typeof configuration.localTarget === 'string') byId('localTarget').value = configuration.localTarget;
      if (typeof configuration.nasMappedTarget === 'string') byId('nasMappedTarget').value = configuration.nasMappedTarget;
      if (['capture-date', 'import-date', 'preserve'].includes(configuration.targetNamingRule)) byId('targetNamingRule').value = configuration.targetNamingRule;
      if (typeof configuration.autoStartOnLogin === 'boolean') byId('autoStartOnLogin').checked = configuration.autoStartOnLogin;
    }
    if (!configured) {
      setupFirstRun = true;
      byId('app').classList.add('first-run');
      if (currentScreen !== 'setup') openSetup({ firstRun: true });
      if (typeof configuration.recoveryNotice === 'string' && configuration.recoveryNotice.trim()) {
        byId('setupError').textContent = configuration.recoveryNotice.trim().slice(0, 360);
        showToast('原设置已保留，请重新完成设置');
      }
    } else if (wasAutomaticFirstRun) {
      setupFirstRun = false;
      byId('app').classList.remove('first-run');
      if (currentScreen === 'setup') showScreen('home');
    }
    if (currentScreen === 'home') applyMediaHomeNarrative();
  }

  function formatBytes(value) {
    if (!Number.isFinite(value) || value < 0) return '--';
    const units = ['B', 'KiB', 'MiB', 'GiB', 'TiB'];
    let amount = value;
    let index = 0;
    while (amount >= 1024 && index < units.length - 1) { amount /= 1024; index += 1; }
    const digits = index === 0 || amount >= 100 ? 0 : amount >= 10 ? 1 : 2;
    return `${amount.toFixed(digits)} ${units[index]}`;
  }

  function formatSpeed(value) {
    if (!Number.isFinite(value) || value <= 0) return '--';
    return `${formatBytes(value)}/s`;
  }

  function formatEta(seconds) {
    if (!Number.isFinite(seconds) || seconds < 0) return '--';
    const total = Math.min(Math.floor(seconds), 3599999);
    const hours = Math.floor(total / 3600);
    const minutes = Math.floor((total % 3600) / 60);
    const secs = total % 60;
    return hours > 0
      ? `${hours.toString().padStart(2,'0')}:${minutes.toString().padStart(2,'0')}:${secs.toString().padStart(2,'0')}`
      : `${minutes.toString().padStart(2,'0')}:${secs.toString().padStart(2,'0')}`;
  }

  function isDigit(character) {
    return character >= '0' && character <= '9';
  }

  function staticPartClass(character) {
    if (/\s/u.test(character)) return 'motion-part motion-static motion-static--space';
    if (/[.:/\-]/u.test(character)) return 'motion-part motion-static motion-static--punctuation';
    return 'motion-part motion-static motion-static--unit';
  }

  function createDigitLayer(character) {
    const layer = document.createElement('span');
    layer.className = 'motion-digit-layer';
    layer.textContent = character;
    return layer;
  }

  function createMetricPart(character) {
    if (!isDigit(character)) {
      const part = document.createElement('span');
      part.className = staticPartClass(character);
      part.dataset.motionKind = 'static';
      part.textContent = character;
      return part;
    }

    const slot = document.createElement('span');
    slot.className = 'motion-part motion-digit-group motion-digit-slot';
    slot.dataset.motionKind = 'digit';
    slot.dataset.motionDigit = character;
    const anchor = document.createElement('span');
    anchor.className = 'motion-digit-anchor';
    anchor.textContent = '8';
    const viewport = document.createElement('span');
    viewport.className = 'motion-digit-viewport';
    const layer = createDigitLayer(character);
    layer.dataset.motionCurrent = 'true';
    viewport.append(layer);
    slot.append(anchor, viewport);
    return slot;
  }

  function animateDigit(slot, character) {
    const previousCharacter = slot.dataset.motionDigit || character;
    if (previousCharacter === character) return;
    const viewport = slot.querySelector('.motion-digit-viewport');
    const outgoing = viewport.querySelector('[data-motion-current="true"]');
    const incoming = createDigitLayer(character);
    incoming.dataset.motionCurrent = 'true';
    if (outgoing) delete outgoing.dataset.motionCurrent;
    viewport.append(incoming);
    slot.dataset.motionDigit = character;

    if (window.matchMedia('(prefers-reduced-motion: reduce)').matches || typeof incoming.animate !== 'function') {
      if (outgoing) outgoing.remove();
      return;
    }

    const direction = Number(character) >= Number(previousCharacter) ? 1 : -1;
    const enterTravel = direction * MOTION_VALUE.travelPercent;
    const enterMid = direction * MOTION_VALUE.midpointPercent;
    const exitMid = -direction * MOTION_VALUE.midpointPercent;
    const exitTravel = -direction * MOTION_VALUE.travelPercent;
    incoming.animate([
      { transform: `translate3d(0,${enterTravel}%,0)`, filter: `blur(${MOTION_VALUE.maxBlurPx}px)`, opacity: 0, offset: 0 },
      { transform: `translate3d(0,${enterMid}%,0)`, filter: `blur(${MOTION_VALUE.midpointBlurPx}px)`, opacity: .88, offset: MOTION_VALUE.enterMidpoint },
      { transform: 'translate3d(0,0,0)', filter: 'blur(0)', opacity: 1, offset: 1 }
    ], { duration: MOTION_VALUE.durationMs, easing: MOTION_VALUE.easing, fill: 'both' });
    if (outgoing) {
      const animation = outgoing.animate([
        { transform: 'translate3d(0,0,0)', filter: 'blur(0)', opacity: 1, offset: 0 },
        { transform: `translate3d(0,${exitMid}%,0)`, filter: `blur(${MOTION_VALUE.midpointBlurPx}px)`, opacity: .62, offset: MOTION_VALUE.exitMidpoint },
        { transform: `translate3d(0,${exitTravel}%,0)`, filter: `blur(${MOTION_VALUE.maxBlurPx}px)`, opacity: 0, offset: 1 }
      ], { duration: MOTION_VALUE.durationMs, easing: MOTION_VALUE.easing, fill: 'both' });
      animation.finished.then(() => outgoing.remove(), () => outgoing.remove());
    }
  }

  function renderMetric(element, text, accessibleName) {
    if (element.dataset.motionInitialized !== 'true') {
      while (element.firstChild) element.firstChild.remove();
      element.dataset.motionInitialized = 'true';
    }
    const value = String(text);
    const characters = [...value];
    element.classList.add('motion-value');
    const existing = [...element.children];

    characters.forEach((character, index) => {
      const expectedKind = isDigit(character) ? 'digit' : 'static';
      let part = existing[index];
      if (!part || part.dataset.motionKind !== expectedKind) {
        const replacement = createMetricPart(character);
        if (part) part.replaceWith(replacement);
        else element.append(replacement);
        part = replacement;
      } else if (expectedKind === 'digit') {
        animateDigit(part, character);
      } else {
        part.className = staticPartClass(character);
        if (part.textContent !== character) part.textContent = character;
      }
    });
    while (element.children.length > characters.length) element.lastElementChild.remove();
    element.dataset.metricValue = value;
    if (accessibleName) element.setAttribute('aria-label', accessibleName);
  }

  function queueMetricRender(element, text, accessibleName) {
    pendingMetricRenders.set(element, { text, accessibleName });
  }

  function flushMetricRenders() {
    if (metricRenderTimer) {
      window.clearTimeout(metricRenderTimer);
      metricRenderTimer = 0;
    }
    pendingMetricRenders.forEach((metric, element) => renderMetric(element, metric.text, metric.accessibleName));
    pendingMetricRenders.clear();
    lastMetricRenderAt = window.performance.now();
  }

  function scheduleMetricFlush() {
    const elapsed = window.performance.now() - lastMetricRenderAt;
    if (elapsed >= METRIC_RENDER_INTERVAL_MS) {
      flushMetricRenders();
      return;
    }
    if (!metricRenderTimer) {
      metricRenderTimer = window.setTimeout(flushMetricRenders, METRIC_RENDER_INTERVAL_MS - elapsed);
    }
  }

  function clearPendingMetricRenders() {
    pendingMetricRenders.clear();
    if (metricRenderTimer) window.clearTimeout(metricRenderTimer);
    metricRenderTimer = 0;
  }

  function renderHostReady() {
    byId('hostDot').classList.remove('offline', 'connecting');
    byId('hostLabel').textContent = '桌面应用已就绪';
  }

  function normalizeMediaTextList(value) {
    return Array.isArray(value)
      ? value.map(item => safeText(item)).filter(Boolean).slice(0, 32)
      : [];
  }

  function nonNegativeNumber(value, fallback = 0) {
    return Number.isFinite(value) && value >= 0 ? value : fallback;
  }

  function normalizeMediaItem(value) {
    if (!isPlainObject(value)) return null;
    const queuePosition = Number.isInteger(value.queuePosition) && value.queuePosition >= 0
      ? value.queuePosition
      : null;
    return {
      mountSessionId: safeText(value.mountSessionId),
      volumeKey: safeText(value.volumeKey),
      driveLetter: safeText(value.driveLetter),
      fileSystem: safeText(value.fileSystem),
      capacityBytes: nonNegativeNumber(value.capacityBytes),
      presenceState: safeText(value.presenceState, 'detecting'),
      eligibilityState: safeText(value.eligibilityState, 'checking'),
      identityState: safeText(value.identityState, 'unknown'),
      cardInstanceId: safeText(value.cardInstanceId),
      cardDisplayName: safeText(value.cardDisplayName),
      expectedDirectories: normalizeMediaTextList(value.expectedDirectories),
      observedCandidateDirectories: normalizeMediaTextList(value.observedCandidateDirectories),
      workState: safeText(value.workState, 'awaiting_action'),
      queuePosition,
      safetyConclusion: safeText(value.safetyConclusion, 'no_backup_conclusion'),
      reasonCode: safeText(value.reasonCode),
      primaryAction: safeText(value.primaryAction),
      availableActions: normalizeMediaTextList(value.availableActions),
      overallPercent: clampPercent(nonNegativeNumber(value.overallPercent)),
      detail: safeText(value.detail)
    };
  }

  function normalizeMediaStatus(payload) {
    if (!isPlainObject(payload) || !Array.isArray(payload.media) || !Number.isFinite(payload.revision) || payload.revision < 0) {
      throw new Error('素材卡状态格式无效');
    }
    return {
      revision: Math.floor(payload.revision),
      activeMountSessionId: safeText(payload.activeMountSessionId),
      media: payload.media.map(normalizeMediaItem).filter(Boolean)
    };
  }

  function mediaHasAction(item, action) {
    return item.primaryAction === action || item.availableActions.includes(action);
  }

  function mediaIsComplete(item) {
    return ['complete', 'completed'].includes(item.workState)
      || ['approved_material_verified', 'backup_verified', 'safe_to_remove'].includes(item.safetyConclusion);
  }

  function mediaNeedsAttention(item) {
    if (item.presenceState === 'detecting' || item.eligibilityState === 'checking' || item.identityState === 'unknown') return false;
    if (mediaHasAction(item, 'configure_card_scope') || mediaHasAction(item, 'defer_current_card')) return true;
    if (['awaiting_action', 'needs_confirmation', 'blocked', 'recovery_required', 'failed'].includes(item.workState)) return true;
    return ['needs_confirmation', 'conflict', 'legacy_profile_missing', 'recovery_required'].includes(item.identityState)
      || ['no_approved_directory', 'inspection_failed', 'scope_changed'].includes(item.eligibilityState);
  }

  function mediaCategory(item) {
    if (item.presenceState === 'removed') return 'current';
    if (mediaNeedsAttention(item)) return 'attention';
    if (mediaIsComplete(item)) return 'completed';
    if ((item.queuePosition !== null && item.queuePosition > 0) || item.workState === 'queued') return 'queued';
    return 'current';
  }

  function mediaIdentityLabel(item) {
    const labels = {
      new: '新素材卡',
      new_card: '新素材卡',
      known: '已识别的素材卡',
      known_card: '已识别的素材卡',
      auto_associate: '已识别的素材卡',
      needs_confirmation: '身份待确认',
      conflict: '身份冲突',
      legacy_profile_missing: '历史卡档案待恢复',
      legacy_card_needs_profile: '历史卡档案待恢复',
      recovery_required: '需要恢复',
      unknown: '正在识别身份'
    };
    return labels[item.identityState] || '正在识别身份';
  }

  function mediaEligibilityLabel(item) {
    const labels = {
      checking: '正在检查素材范围',
      eligible: '已找到批准的素材范围',
      approved_range_found: '已找到批准的素材范围',
      matching_files: '已找到匹配素材',
      matching_files_found: '已找到匹配素材',
      no_matching_files: '当前范围没有匹配素材',
      no_approved_directory: '未找到批准的素材目录',
      approved_range_missing: '未找到批准的素材目录',
      inspection_failed: '无法读取素材范围',
      scope_changed: '素材目录发生变化'
    };
    return labels[item.eligibilityState] || '正在检查素材范围';
  }

  function mediaWorkLabel(item) {
    const labels = {
      detecting: '正在检测',
      awaiting_action: '等待你的决定',
      queued: '等待处理',
      scanning: '正在扫描',
      copying: '正在保存',
      verifying: '正在完整校验',
      blocked: '需要处理',
      recovery_required: '等待恢复',
      complete: '本次已完成',
      completed: '本次已完成',
      failed: '未安全完成'
    };
    if (item.presenceState === 'removed') return '卡已移除';
    return labels[item.workState] || '正在处理';
  }

  function mediaSafetyLabel(item) {
    const labels = {
      approved_material_verified: '当前素材范围已备份并完整校验',
      backup_verified: '当前素材范围已备份并校验',
      safe_to_remove: '当前素材范围可安全移除',
      keep_inserted: '请保持当前卡连接',
      no_backup_conclusion: '尚无安全结论'
    };
    return labels[item.safetyConclusion] || '尚无安全结论';
  }

  function mediaCardName(item) {
    if (item.cardDisplayName) return item.cardDisplayName;
    if (item.identityState === 'new') return '新素材卡';
    if (item.cardInstanceId) return `素材卡 ${item.cardInstanceId.slice(0, 8)}`;
    return item.driveLetter ? `${item.driveLetter} 存储卡` : '外接存储卡';
  }

  function mediaScopeLabel(item) {
    if (item.expectedDirectories.length > 0) return `当前素材范围：${item.expectedDirectories.join('、')}`;
    if (item.observedCandidateDirectories.length > 0) return `发现目录：${item.observedCandidateDirectories.join('、')}`;
    return '';
  }

  function mediaCompletionMessage(item) {
    const scope = mediaScopeLabel(item) || '当前纳入的素材范围';
    const verified = ['approved_material_verified', 'backup_verified'].includes(item.safetyConclusion);
    return verified
      ? `${scope}已完整备份并校验。其他目录和不匹配文件不在本次结论内。`
      : `${scope}已有当前任务的可移除结论；其他目录和不匹配文件不在本次结论内。`;
  }

  function mediaDetailText(item) {
    if (mediaIsComplete(item)) return mediaCompletionMessage(item);
    const parts = [mediaIdentityLabel(item), mediaEligibilityLabel(item), mediaWorkLabel(item)];
    if (item.queuePosition !== null && item.queuePosition > 0) parts.push(`队列第 ${item.queuePosition} 位`);
    if (item.detail) parts.push(item.detail);
    return [...new Set(parts)].join(' · ');
  }

  function mediaStateClass(item, activeMountSessionId) {
    if (item.presenceState === 'removed') return 'removed';
    if (mediaNeedsAttention(item)) return 'attention';
    if (mediaIsComplete(item)) return 'complete';
    if (item.mountSessionId && item.mountSessionId === activeMountSessionId) return 'active';
    if (mediaCategory(item) === 'queued') return 'queued';
    return 'neutral';
  }

  function appendMediaAction(actions, label, handler, primary = false) {
    const button = document.createElement('button');
    button.type = 'button';
    button.className = primary ? 'btn primary compact' : 'btn secondary compact';
    button.textContent = label;
    button.addEventListener('click', handler);
    actions.appendChild(button);
  }

  function configureMediaScope(item) {
    const knownCard = item.cardInstanceId
      ? knownCardsForConfiguration(latestConfiguration).find(card =>
        card.cardInstanceId === item.cardInstanceId)
      : null;
    if (knownCard) {
      beginManagedCardScopeEdit(knownCard, mediaCardName(item));
      return;
    }
    openSetup({ step: 0 });
  }

  function renderMediaCard(item, activeMountSessionId) {
    const article = document.createElement('article');
    article.className = `media-card media-card--${mediaStateClass(item, activeMountSessionId)}`;
    const header = document.createElement('div');
    header.className = 'media-card-header';
    const identity = document.createElement('div');
    identity.className = 'media-card-identity';
    const name = document.createElement('strong');
    name.textContent = mediaCardName(item);
    const drive = document.createElement('span');
    drive.textContent = item.driveLetter || '外接存储';
    identity.append(name, drive);
    const state = document.createElement('span');
    state.className = 'media-state-pill';
    state.textContent = mediaWorkLabel(item);
    header.append(identity, state);

    const meta = document.createElement('p');
    meta.className = 'media-card-meta';
    const metaParts = [];
    if (item.fileSystem) metaParts.push(item.fileSystem);
    if (item.capacityBytes > 0) metaParts.push(formatBytes(item.capacityBytes));
    metaParts.push(mediaSafetyLabel(item));
    meta.textContent = metaParts.join(' · ');

    const detail = document.createElement('p');
    detail.className = 'media-card-detail';
    detail.textContent = mediaDetailText(item);
    article.append(header, meta, detail);

    const scope = mediaScopeLabel(item);
    if (scope && !mediaIsComplete(item)) {
      const scopeNode = document.createElement('small');
      scopeNode.className = 'media-card-scope';
      scopeNode.textContent = scope;
      article.appendChild(scopeNode);
    }
    if (item.overallPercent > 0 && !mediaIsComplete(item)) {
      const progress = document.createElement('div');
      progress.className = 'media-card-progress';
      const bar = document.createElement('i');
      bar.style.width = `${Math.round(item.overallPercent)}%`;
      progress.appendChild(bar);
      const label = document.createElement('small');
      label.textContent = `${Math.round(item.overallPercent)}%`;
      const progressRow = document.createElement('div');
      progressRow.className = 'media-progress-row';
      progressRow.append(progress, label);
      article.appendChild(progressRow);
    }

    const actions = document.createElement('div');
    actions.className = 'media-card-actions';
    const actionNames = [...new Set([item.primaryAction, ...item.availableActions].filter(Boolean))];
    if (actionNames.includes('configure_card_scope')) {
      appendMediaAction(actions, '调整素材范围', () => configureMediaScope(item), true);
    }
    if (actionNames.includes('defer_current_card')) {
      appendMediaAction(actions, '暂缓此卡', () => postCommand('media.defer', { mountSessionId: item.mountSessionId }));
    }
    if (actionNames.includes('refresh') || (mediaNeedsAttention(item) && actions.childElementCount === 0)) {
      appendMediaAction(actions, '刷新状态', () => postCommand('status.refresh'));
    }
    if (actions.childElementCount > 0) article.appendChild(actions);
    return article;
  }

  function renderMediaGroup(name, items, activeMountSessionId) {
    const group = document.querySelector(`[data-media-group="${name}"]`);
    const list = byId(`media${name[0].toUpperCase()}${name.slice(1)}List`);
    const count = byId(`media${name[0].toUpperCase()}${name.slice(1)}Count`);
    group.hidden = items.length === 0;
    count.textContent = String(items.length);
    list.replaceChildren(...items.map(item => renderMediaCard(item, activeMountSessionId)));
  }

  function selectedMediaForHome(status) {
    const attention = status.media.find(mediaNeedsAttention);
    if (attention) return attention;
    const active = status.media.find(item => item.mountSessionId && item.mountSessionId === status.activeMountSessionId);
    if (active) return active;
    return status.media.find(item => item.presenceState !== 'removed') || status.media[0] || null;
  }

  function activeDeferrableMedia() {
    if (!latestMediaStatus || !latestMediaStatus.activeMountSessionId) return null;
    const active = latestMediaStatus.media.find(item =>
      item.mountSessionId === latestMediaStatus.activeMountSessionId);
    return active && mediaHasAction(active, 'defer_current_card') ? active : null;
  }

  function updateFailureDeferButton() {
    byId('failureDeferCardBtn').hidden = activeDeferrableMedia() === null;
  }

  function applyMediaHomeNarrative() {
    if (!latestMediaStatus || latestMediaStatus.media.length === 0) return;
    const item = selectedMediaForHome(latestMediaStatus);
    if (!item || !(latestConfiguration && latestConfiguration.configured === true)) return;
    const card = mediaCardName(item);
    let headline = `已检测到 ${item.driveLetter || '存储卡'}，正在识别素材卡`;
    if (mediaNeedsAttention(item)) headline = `${card}需要你的决定`;
    else if (mediaIsComplete(item)) headline = `${card}的本次素材范围已完成`;
    else if (mediaCategory(item) === 'queued') headline = `${card}正在等待处理`;
    else if (item.workState === 'copying' || item.workState === 'verifying') headline = `${card}${mediaWorkLabel(item)}`;
    byId('homeEyebrow').textContent = `已检测到 ${latestMediaStatus.media.length} 张存储卡`;
    byId('waitingHeadline').textContent = headline;
    byId('waitingDescription').textContent = mediaDetailText(item);
  }

  function currentCompletionMedia() {
    if (!latestMediaStatus) return null;
    const active = latestMediaStatus.media.find(item => item.mountSessionId && item.mountSessionId === latestMediaStatus.activeMountSessionId);
    return active && mediaIsComplete(active) ? active : latestMediaStatus.media.find(mediaIsComplete) || null;
  }

  function renderMediaStatus(payload) {
    const status = normalizeMediaStatus(payload);
    latestMediaStatus = status;
    const panel = byId('mediaStatusPanel');
    panel.hidden = false;
    const mounted = status.media.filter(item => item.presenceState !== 'removed');
    if (status.media.length === 0) {
      byId('mediaStatusTitle').textContent = '未检测到已插入的外接素材卡';
      byId('mediaStatusSummary').textContent = '插卡后会先说明它是新卡、已认识的卡，还是需要恢复的历史卡。';
    } else {
      byId('mediaStatusTitle').textContent = `已检测到 ${mounted.length} 张已插入的存储卡`;
      byId('mediaStatusSummary').textContent = '每张卡都有独立状态；未处理、排队或需要决定的卡不会显示为已完成。';
    }
    const groups = { current: [], queued: [], attention: [], completed: [] };
    mounted.forEach(item => groups[mediaCategory(item)].push(item));
    groups.queued.sort((first, second) => (first.queuePosition || Number.MAX_SAFE_INTEGER) - (second.queuePosition || Number.MAX_SAFE_INTEGER));
    Object.entries(groups).forEach(([name, items]) => renderMediaGroup(name, items, status.activeMountSessionId));
    applyMediaHomeNarrative();
    updateFailureDeferButton();
  }

  function renderWaiting(payload) {
    const configured = latestConfiguration && latestConfiguration.configured === true;
    byId('homeEyebrow').textContent = configured ? '自动同步已就绪' : '需要完成首次配置';
    byId('waitingHeadline').textContent = safeText(payload.headline, configured ? '等待插入素材卡' : '先完成首次配置');
    byId('waitingDescription').textContent = safeText(payload.description, configured
      ? '插卡后会自动开始，不需要点击“开始”。同步期间请保持素材卡和所选目标可用。'
      : '跟随首次引导完成素材范围和保存位置，AutoCardSync 才会开始等待插卡。');
    if (!configured) {
      if (currentScreen !== 'setup') openSetup({ firstRun: true });
      return;
    }
    applyMediaHomeNarrative();
    if (currentScreen !== 'setup') showScreen('home');
  }

  function validateTarget(target) {
    if (!isPlainObject(target) || !['local', 'mappedNas'].includes(target.kind)) throw new Error('保存目标状态无效');
    if (!Object.prototype.hasOwnProperty.call(TARGET_STATE_LABELS, target.state)) throw new Error('保存目标阶段无效');
    for (const key of ['copyPercent', 'verificationPercent']) {
      if (!Number.isFinite(target[key]) || target[key] < 0 || target[key] > 100) throw new Error('保存目标进度无效');
    }
    for (const key of [
      'bytesPerSecond', 'verificationBytesPerSecond', 'copyEtaSeconds',
      'verificationEtaSeconds', 'verificationBytes', 'verificationTotalBytes'
    ]) {
      if (!Number.isFinite(target[key]) || target[key] < 0) throw new Error('保存目标速度或字节进度无效');
    }
    if (target.verificationBytes > target.verificationTotalBytes) throw new Error('保存目标校验字节进度超出范围');
    return target;
  }

  function renderTarget(target) {
    const local = target.kind === 'local';
    const prefix = local ? 'local' : 'nas';
    const card = byId(`${prefix}TargetCard`);
    card.hidden = false;
    card.classList.toggle('failed', target.state === 'failed');
    card.classList.toggle('complete', target.state === 'complete');
    byId(`${prefix}TargetState`).textContent = TARGET_STATE_LABELS[target.state];
    byId(`${prefix}CopyPercent`).textContent = `${Math.round(target.copyPercent)}%`;
    byId(`${prefix}CopyBar`).style.width = `${clampPercent(target.copyPercent)}%`;
    byId(`${prefix}VerifyBar`).style.width = `${clampPercent(target.verificationPercent)}%`;
    const detail = safeText(target.detail, target.state === 'verifying' ? '正在从最终文件完整读取并校验' : TARGET_STATE_LABELS[target.state]);
    const currentFile = safeText(target.currentFile, '');
    const detailWithFile = currentFile && !detail.includes(currentFile)
      ? `${detail}：${currentFile}`
      : detail;
    const copyEta = target.copyEtaSeconds > 0 ? formatEta(target.copyEtaSeconds) : '计算中';
    const verificationEta = target.verificationEtaSeconds > 0 ? formatEta(target.verificationEtaSeconds) : '计算中';
    const metricDetail = target.state === 'copying'
      ? `${detailWithFile} · 写入速度 ${formatSpeed(target.bytesPerSecond)} · 本阶段预计 ${copyEta}`
      : target.state === 'verifying'
        ? `${detailWithFile} · 已校验 ${formatBytes(target.verificationBytes)} / ${formatBytes(target.verificationTotalBytes)} · 校验速度 ${formatSpeed(target.verificationBytesPerSecond)} · 本阶段预计 ${verificationEta}`
        : detailWithFile;
    byId(`${prefix}TargetDetail`).textContent = metricDetail;
  }

  function renderCopying(payload) {
    const requiredNumbers = ['overallPercent', 'etaSeconds', 'completedFiles', 'totalFiles'];
    if (requiredNumbers.some(key => !Number.isFinite(payload[key]) || payload[key] < 0)) throw new Error('同步进度数值无效');
    if (payload.overallPercent > 100 || payload.completedFiles > payload.totalFiles) throw new Error('同步进度超出范围');
    const targetMode = normalizeTargetMode(payload.targetMode);
    const requiresLocal = targetMode !== 'nas-only';
    const requiresNas = targetMode !== 'local-only';
    const requiredCount = (requiresLocal ? 1 : 0) + (requiresNas ? 1 : 0);
    if (!Array.isArray(payload.targets) || payload.targets.length !== requiredCount) throw new Error('所选保存目标状态不完整');
    const targets = payload.targets.map(validateTarget);
    const local = targets.find(target => target.kind === 'local');
    const mappedNas = targets.find(target => target.kind === 'mappedNas');
    if ((requiresLocal && !local) || (requiresNas && !mappedNas)) throw new Error('所选保存目标状态不完整');
    byId('localTargetCard').hidden = !requiresLocal;
    byId('nasTargetCard').hidden = !requiresNas;

    const percent = clampPercent(payload.overallPercent);
    latestStatus = payload;
    byId('progressOrb').style.setProperty('--progress-rendered', String(percent));
    queueMetricRender(byId('progressValue'), `${Math.round(percent)}%`, `总体进度 ${Math.round(percent)} 百分比`);
    byId('progressStage').textContent = PHASE_LABELS[payload.phase] || '正在处理';
    byId('taskId').textContent = safeText(payload.taskLabel, safeText(payload.taskId, '当前任务'));
    byId('currentFile').textContent = safeText(payload.currentFile, '正在准备下一个文件…');
    byId('taskDescription').textContent = percent >= 100
      ? '文件复制进度已到 100%，正在等待所选目标完成临时和最终校验；现在仍不能拔卡。'
      : safeText(payload.description, '正在将批准的素材保存到所选目标。');
    const sourceSpeed = Number.isFinite(payload.sourceBytesPerSecond) && payload.sourceBytesPerSecond > 0
      ? payload.sourceBytesPerSecond
      : Number.NaN;
    const sourceAverageSpeed = Number.isFinite(payload.sourceAverageBytesPerSecond) && payload.sourceAverageBytesPerSecond > 0
      ? payload.sourceAverageBytesPerSecond
      : Number.NaN;
    queueMetricRender(byId('currentSpeed'), formatSpeed(sourceSpeed), `素材卡读取速度 ${formatSpeed(sourceSpeed)}`);
    queueMetricRender(byId('averageSpeed'), formatSpeed(sourceAverageSpeed), `素材卡平均速度 ${formatSpeed(sourceAverageSpeed)}`);
    queueMetricRender(byId('eta'), formatEta(payload.etaSeconds), `预计剩余 ${formatEta(payload.etaSeconds)}`);
    queueMetricRender(byId('fileProgress'), `${Math.floor(payload.completedFiles)} / ${Math.floor(payload.totalFiles)}`, `已处理 ${Math.floor(payload.completedFiles)} 个，共 ${Math.floor(payload.totalFiles)} 个`);
    scheduleMetricFlush();
    if (local) renderTarget(local);
    if (mappedNas) renderTarget(mappedNas);
    byId('taskSafetyPill').textContent = '不可拔卡';
    byId('taskSafetyPill').className = 'pill failed';
    showScreen('task');
  }

  function renderFailure(
    title,
    what,
    safety,
    next,
    canRestartFresh,
    canReinitializeCard,
    canReassociateCard,
    canConfirmSourceCleanup) {
    byId('failureTitle').textContent = safeText(title, '目前不能确认可以拔卡');
    byId('failureMessage').textContent = 'AutoCardSync 已停止给出成功结论，不会把部分完成或未知状态显示为安全完成。';
    byId('failureWhat').textContent = safeText(what, '桌面应用返回了无效或不完整的状态');
    byId('failureSafety').textContent = safeText(safety, '请保持素材卡原位，不要根据进度数字拔卡');
    byId('restartFreshBtn').hidden = canRestartFresh !== true;
    byId('reassociateCardBtn').hidden = canReassociateCard !== true;
    byId('reinitializeCardBtn').hidden = canReinitializeCard !== true;
    byId('confirmSourceCleanupBtn').hidden = canConfirmSourceCleanup !== true;
    updateFailureDeferButton();
    byId('failureNext').textContent = safeText(next, '恢复原来的连接后重试；无法确认的任务不会显示安全完成');
    showScreen('failure');
  }

  function safetyComplete(payload) {
    if (!Number.isInteger(payload.totalFiles) || payload.totalFiles <= 0) return false;
    if (!Number.isInteger(payload.completedFiles) || payload.completedFiles !== payload.totalFiles) return false;
    if (!isPlainObject(payload.safety)) return false;
    const safety = payload.safety;
    const booleanChecks = [
      'manifestFrozen',
      'sourceReadOnly',
      'allIncludedFilesAccountedFor',
      'finalObjectsSafelyAvailable',
      'sourceIdentityUnchanged',
      'targetIdentitiesUnchanged',
      'localCompletionReceiptPersisted',
      'safeToRemoveCard'
    ];
    if (booleanChecks.some(key => safety[key] !== true)) return false;
    const targetMode = normalizeTargetMode(payload.targetMode);
    const requiresLocal = targetMode !== 'nas-only';
    const requiresNas = targetMode !== 'local-only';
    if (requiresLocal && safety.localTargetFullRereadSha256 !== 'PASS') return false;
    if (requiresNas && safety.nasTargetFullRereadSha256 !== 'PASS') return false;
    if (safety.failedIncludedFiles !== 0 || safety.pendingIncludedFiles !== 0) return false;
    const requiredCount = (requiresLocal ? 1 : 0) + (requiresNas ? 1 : 0);
    if (!Array.isArray(payload.targets) || payload.targets.length !== requiredCount) return false;
    try {
      const targets = payload.targets.map(validateTarget);
      return (!requiresLocal || targets.some(target => target.kind === 'local' && target.state === 'complete' && target.verificationPercent === 100))
        && (!requiresNas || targets.some(target => target.kind === 'mappedNas' && target.state === 'complete' && target.verificationPercent === 100));
    } catch {
      return false;
    }
  }

  function renderBaseline(payload) {
    if (payload.phase !== 'baseline-ready' || payload.baselinePersisted !== true || payload.safeToRemoveCard !== true) {
      renderFailure(
        '素材卡初始化状态不完整',
        '桌面应用尚未提供完整的元数据基线持久化证据',
        '现在仍不能拔卡；不完整的初始化状态不会显示安全结论',
        '保持素材卡原位，然后重新读取状态'
      );
      return;
    }
    if (Array.isArray(payload.targets) && payload.targets.length !== 0) throw new Error('初始化状态不得包含目标完成结论');
    byId('baselineTitle').textContent = safeText(payload.headline, '素材卡检查完成');
    byId('baselineMessage').textContent = safeText(payload.description, '已记录当前素材清单，今后仅同步新增素材。');
    byId('baselineInventory').textContent = safeText(payload.inventoryLabel, '当前批准目录和文件类型的清单已记录');
    byId('baselineSafety').textContent = '本次元数据检查已经结束，可以安全拔卡';
    showScreen('baseline');
  }

  function renderComplete(payload) {
    if (!safetyComplete(payload)) {
      renderFailure(
        '安全完成条件尚未全部满足',
        '桌面应用请求显示完成页，但完整保存、最终校验或身份连续性证据不完整',
        '现在仍不能拔卡；100% 进度不能替代安全完成',
        '保持素材卡和所选目标原位，然后重新检查任务状态'
      );
      return;
    }
    const totalFiles = Number.isFinite(payload.totalFiles) ? Math.max(0, Math.floor(payload.totalFiles)) : 0;
    const targetMode = normalizeTargetMode(payload.targetMode);
    const requiresLocal = targetMode !== 'nas-only';
    const requiresNas = targetMode !== 'local-only';
    document.querySelector('[data-completion-target="local"]').hidden = !requiresLocal;
    document.querySelector('[data-completion-target="nas"]').hidden = !requiresNas;
    byId('completeMessage').textContent = safeText(payload.description, '所选保存目标已完成发布和最终校验。');
    byId('completeFiles').textContent = `${totalFiles} 个文件全部有结果，失败 0，待处理 0`;
    if (requiresLocal) byId('completeLocal').textContent = '最终文件已完整重读，SHA-256 校验通过';
    if (requiresNas) byId('completeNas').textContent = '最终文件已完整重读，SHA-256 校验通过';
    byId('completeReceipt').textContent = safeText(payload.completionReceiptLabel, '本次完成记录已安全保存到本机');
    const completionMedia = currentCompletionMedia();
    const completionScope = completionMedia ? mediaScopeLabel(completionMedia) : '';
    byId('completeScope').textContent = completionScope
      ? `${completionScope}；本次完整备份与校验只覆盖此范围，其他目录和不匹配文件不在此结论内。`
      : '本次完整备份与校验仅适用于纳入任务的素材目录和文件类型；其他文件不在此结论内。';
    showScreen('complete');
  }

  function renderStatus(payload) {
    currentOperationId = typeof payload?.operationId === 'string' && payload.operationId.length > 0 ? payload.operationId : null;
    if (!isPlainObject(payload) || !['waiting', 'copying', 'failure', 'complete', 'baseline'].includes(payload.view)) throw new Error('桌面状态类型无效');
    renderHostReady();
    if (isPlainObject(payload.configuration)) renderConfiguration(payload.configuration);
    // Status events continue while the user edits or submits a card-specific
    // scope. Keep that explicit workflow visible until its own response decides
    // whether to return home; otherwise a waiting/failure push hides the actual
    // save result and makes the operation appear to do nothing.
    if (currentScreen === 'setup') {
      latestStatus = payload;
      return;
    }
    if (payload.view !== 'copying') clearPendingMetricRenders();
    if (payload.view === 'waiting') renderWaiting(payload);
    else if (payload.view === 'copying') renderCopying(payload);
    else if (payload.view === 'baseline') renderBaseline(payload);
    else if (payload.view === 'failure') {
      const failure = isPlainObject(payload.failure) ? payload.failure : {};
      renderFailure(
        failure.title,
        failure.what,
        failure.safety,
        failure.next,
        failure.canRestartFresh,
        failure.canReinitializeCard,
        failure.canReassociateCard,
        failure.canConfirmSourceCleanup);
    } else renderComplete(payload);
  }

  function responsePayload(message) {
    if (!isPlainObject(message.payload)) throw new Error('桌面响应内容无效');
    if (typeof message.payload.success !== 'boolean') throw new Error('桌面响应结果无效');
    return message.payload;
  }

  function handleResponse(message) {
    const requestId = typeof message.requestId === 'string' ? message.requestId.toLowerCase() : '';
    const pending = pendingRequests.get(requestId);
    if (!pending) return;
    pendingRequests.delete(requestId);
    window.clearTimeout(pending.timeoutId);
    const response = responsePayload(message);

    const setupSave = SENSITIVE_COMMAND_TYPES.has(pending.type);
    if (setupSave) byId('setupSubmitBtn').disabled = false;
    if (!response.success) {
      const error = safeText(response.error, '桌面应用拒绝了请求。');
      if (setupSave) {
        byId('setupError').textContent = error;
        if (currentScreen !== 'setup') showScreen('setup');
      }
      else showToast(error);
      return;
    }

    if (pending.type === 'configuration.get' && isPlainObject(response.data)) renderConfiguration(response.data);
    if (pending.type === 'card.profile.rename' && isPlainObject(response.data)) {
      renderConfiguration(response.data, { forceFormReset: true });
      showToast(safeText(response.message, '素材卡名称已更新'));
    }
    if (pending.type === 'configuration.save') {
      renderConfiguration(
        isPlainObject(response.data) ? response.data : { ...pending.context.configuration, configured: true },
        { forceFormReset: true });
      setupFirstRun = false;
      byId('app').classList.remove('first-run');
      showScreen('home');
      showToast(safeText(response.message, '设置已保存，正在检测素材卡'));
    }
    if (pending.type === 'card.profile.configure') {
      const configuration = isPlainObject(response.data) && isPlainObject(response.data.configuration)
        ? response.data.configuration
        : { ...pending.context.configuration, configured: true };
      pendingManagedRebind = null;
      renderConfiguration(configuration, { forceFormReset: true });
      setupFirstRun = false;
      byId('app').classList.remove('first-run');
      showScreen('home');
      showToast(safeText(response.message, '素材卡已绑定到新的素材范围'));
    }
    if (pending.type === 'picker.selectFolder') {
      const path = isPlainObject(response.data) ? safeText(response.data.path) : '';
      const fieldId = pending.context && pending.context.fieldId;
      if (path && fieldId === 'approvedSourceDirectories') {
        try {
          addSourceDirectory(path);
          showToast(`已选择 ${safeText(response.data.displayPath, path)}`);
        } catch (exception) {
          byId('setupError').textContent = exception instanceof Error ? exception.message : '源目录无效。';
        }
      } else if (path && ['localTarget', 'nasMappedTarget'].includes(fieldId)) byId(fieldId).value = path;
    }
    if ([
      'status.refresh',
      'task.retry',
      'task.restartFresh',
      'card.reassociate',
      'card.reinitialize',
      'card.profile.reinitialize',
      'card.confirmSourceCleanup',
      'task.cancel',
      'media.defer'
    ].includes(pending.type) && response.message) showToast(response.message);
  }

  function receiveNativeMessage(event) {
    let message = event && event.data;
    try {
      if (typeof message === 'string') {
        if (message.length > MAX_NATIVE_MESSAGE_CHARS) throw new Error('桌面消息超过大小限制');
        message = JSON.parse(message);
      }
      if (!isPlainObject(message) || message.schemaVersion !== SCHEMA_VERSION || typeof message.type !== 'string') throw new Error('桌面消息结构无效');
      if (message.type === 'ui.response') { handleResponse(message); return; }
      if (message.type === 'standalone.configuration') { renderConfiguration(message.payload); return; }
      if (message.type === 'standalone.status') { renderStatus(message.payload); return; }
      if (message.type === 'standalone.mediaStatus') { renderMediaStatus(message.payload); return; }
      throw new Error('桌面消息类型无效');
    } catch (exception) {
      renderFailure(
        '无法确认桌面应用状态',
        exception instanceof Error ? exception.message : '收到无法识别的桌面状态',
        '未知状态不会产生安全完成结论，请保持素材卡原位',
        '重新读取状态；如果问题持续，请关闭窗口后从托盘重新打开'
      );
    }
  }

  document.querySelectorAll('[data-command]').forEach(button => button.addEventListener('click', event => {
    event.stopPropagation();
    postCommand(button.dataset.command);
  }));
  byId('windowDragRegion').addEventListener('pointerdown', event => {
    if (event.button === 0 && !event.target.closest('button')) postCommand('window.drag');
  });
  byId('helpBtn').addEventListener('click', () => openOverlay('helpSheet'));
  byId('settingsBtn').addEventListener('click', () => openSetup());
  byId('openSetupBtn').addEventListener('click', () => openSetup());
  byId('manageCardsBtn').addEventListener('click', () => openSetup({ step: 3 }));
  byId('failureSettingsBtn').addEventListener('click', () => openSetup());
  byId('failureManageCardsBtn').addEventListener('click', () => openSetup({ step: 3 }));
  byId('homeBtn').addEventListener('click', () => { if (!setupFirstRun) showScreen('home'); });
  byId('completeHomeBtn').addEventListener('click', () => showScreen('home'));
  byId('baselineHomeBtn').addEventListener('click', () => showScreen('home'));
  byId('refreshHomeBtn').addEventListener('click', () => postCommand('status.refresh'));
  byId('refreshMediaStatusBtn').addEventListener('click', () => postCommand('status.refresh'));
  byId('refreshTaskBtn').addEventListener('click', () => postCommand('status.refresh'));
  byId('retryTaskBtn').addEventListener('click', () => postCommand('task.retry'));
  byId('failureDeferCardBtn').addEventListener('click', () => {
    const media = activeDeferrableMedia();
    if (media) postCommand('media.defer', { mountSessionId: media.mountSessionId });
  });
  byId('restartFreshBtn').addEventListener('click', () => {
    const confirmed = window.confirm('旧任务记录和未确认状态会被保留，不会删除。AutoCardSync 将按当前卡片内容创建新任务；这可能产生重复副本。确定继续吗？');
    if (confirmed) postCommand('task.restartFresh', { confirmed: true });
  });
  byId('reassociateCardBtn').addEventListener('click', () => {
    const confirmed = window.confirm('仅当这确实是同一张素材卡、只是更换了读卡器或卷端点时继续。AutoCardSync 会保留历史基线并重新检查新增或被改写的素材；不会把当前内容直接吞进新基线。确定继续吗？');
    if (confirmed) postCommand('card.reassociate', { confirmed: true });
  });
  byId('reinitializeCardBtn').addEventListener('click', () => {
    const confirmed = window.confirm('这会重新确认当前素材卡身份和相机模板，并保留旧任务、回执和历史基线。当前批准范围内的文件会重新检查，必要时重新复制，因此可能产生重复副本；源素材仍保持只读。确定继续吗？');
    if (confirmed) postCommand('card.reinitialize', { confirmed: true });
  });
  byId('confirmSourceCleanupBtn').addEventListener('click', () => {
    const confirmed = window.confirm('仅当这些历史文件确实由你主动删除、在相机中清理或格式化后不再存在时继续。AutoCardSync 会保留清理前的基线快照；如果卡内还有新增或修改的素材，会重新复制并完整校验当前批准范围内的全部文件，因此可能产生重复副本。不会删除目标副本。确定继续吗？');
    if (confirmed) postCommand('card.confirmSourceCleanup', { confirmed: true });
  });
  byId('cancelTaskBtn').addEventListener('click', () => {
    byId('cancelConfirm').classList.add('open');
    byId('cancelConfirm').setAttribute('aria-hidden', 'false');
  });
  byId('cancelNoBtn').addEventListener('click', () => {
    byId('cancelConfirm').classList.remove('open');
    byId('cancelConfirm').setAttribute('aria-hidden', 'true');
  });
  byId('cancelYesBtn').addEventListener('click', () => {
    byId('cancelConfirm').classList.remove('open');
    byId('cancelConfirm').setAttribute('aria-hidden', 'true');
    postCommand('task.cancel', { operationId: currentOperationId });
  });
  byId('setupCancelBtn').addEventListener('click', closeSetup);
  byId('setupBackBtn').addEventListener('click', () => showSetupStep(currentSetupStep - 1, false));
  byId('setupNextBtn').addEventListener('click', () => showSetupStep(currentSetupStep + 1, true));
  byId('setupForm').addEventListener('submit', submitSetup);
  document.querySelectorAll('input[name="targetMode"]').forEach(input => input.addEventListener('change', updateTargetModeUi));
  byId('cameraTemplateSelect').addEventListener('change', event => switchCameraTemplate(event.target.value));
  byId('addCameraTemplateBtn').addEventListener('click', addCameraTemplate);
  byId('removeCameraTemplateBtn').addEventListener('click', removeActiveCameraTemplate);
  byId('setDefaultCameraTemplateBtn').addEventListener('click', () => {
    captureActiveCameraTemplate();
    defaultCameraTemplateId = activeCameraTemplateId;
    renderCameraTemplateControls();
  });
  byId('cameraTemplateName').addEventListener('input', event => {
    const template = currentCameraTemplate();
    if (!template) return;
    template.name = event.target.value;
    byId('activeCameraTemplateLabel').textContent = safeText(event.target.value, '未命名模板');
  });
  byId('cameraTemplateName').addEventListener('blur', () => {
    captureActiveCameraTemplate();
    renderCameraTemplateControls();
  });
  byId('addCustomExtensionBtn').addEventListener('click', addCustomExtension);
  byId('customExtensionInput').addEventListener('keydown', event => {
    if (event.key === 'Enter') { event.preventDefault(); addCustomExtension(); }
  });
  document.querySelectorAll('[data-picker]').forEach(button => button.addEventListener('click', () => {
    const fieldId = button.dataset.picker;
    postCommand('picker.selectFolder', { purpose: fieldId }, { fieldId });
  }));
  document.querySelectorAll('[data-close-sheet]').forEach(button => button.addEventListener('click', () => closeOverlay(button.closest('.overlay').id)));
  document.addEventListener('keydown', event => {
    if (event.key !== 'Escape') return;
    const open = document.querySelector('.overlay.open');
    if (open) closeOverlay(open.id);
    else if (byId('cancelConfirm').classList.contains('open')) {
      byId('cancelConfirm').classList.remove('open');
      byId('cancelConfirm').setAttribute('aria-hidden', 'true');
    } else if (currentScreen === 'setup' && !setupFirstRun) closeSetup();
    else postCommand('window.close');
  });
  document.addEventListener('pointermove', event => {
    document.documentElement.style.setProperty('--px', String(event.clientX / Math.max(1, window.innerWidth)));
    document.documentElement.style.setProperty('--py', String(event.clientY / Math.max(1, window.innerHeight)));
    const glow = document.querySelector('.cursor-glow');
    if (glow) glow.style.transform = `translate(${event.clientX - 110}px,${event.clientY - 110}px)`;
  }, { passive: true });

  if (bridge) {
    bridge.addEventListener('message', receiveNativeMessage);
    renderHostReady();
    postCommand('ui.ready', { uiVersion: 1 });
    postCommand('configuration.get');
    postCommand('status.refresh');
  } else {
    byId('hostDot').classList.remove('connecting');
    byId('hostDot').classList.add('offline');
    byId('hostLabel').textContent = '桌面应用连接未就绪';
    byId('configurationDot').classList.add('offline');
    byId('configurationLabel').textContent = '设置状态不可用';
  }
})();

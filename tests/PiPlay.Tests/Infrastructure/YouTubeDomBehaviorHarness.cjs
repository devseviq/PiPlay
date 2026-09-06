"use strict";

const fs = require("node:fs");
const vm = require("node:vm");

const input = JSON.parse(fs.readFileSync(0, "utf8"));
const failures = [];
let passed = 0;

function assert(condition, message) {
  if (!condition) throw new Error(message);
}

function equal(actual, expected, message) {
  if (!Object.is(actual, expected)) {
    throw new Error(`${message} (expected ${JSON.stringify(expected)}, got ${JSON.stringify(actual)})`);
  }
}

function scenario(name, action) {
  try {
    action();
    passed++;
    process.stdout.write(`PASS ${name}\n`);
  } catch (error) {
    failures.push(`${name}: ${error && error.stack ? error.stack : error}`);
    process.stderr.write(`FAIL ${name}\n`);
  }
}

class EventHub {
  constructor() {
    this.listeners = new Map();
  }

  addEventListener(type, listener) {
    const listeners = this.listeners.get(type) || [];
    listeners.push(listener);
    this.listeners.set(type, listeners);
  }

  removeEventListener(type, listener) {
    const listeners = this.listeners.get(type) || [];
    this.listeners.set(type, listeners.filter(candidate => candidate !== listener));
  }

  emit(type, event = {}) {
    event.type = type;
    event.currentTarget = this;
    for (const listener of [...(this.listeners.get(type) || [])]) listener.call(this, event);
    return event;
  }

  listenerCount(type) {
    return (this.listeners.get(type) || []).length;
  }
}

class FakeStyle {
  constructor() {
    this.values = new Map();
  }

  getPropertyValue(name) {
    return this.values.get(name) || "";
  }

  setProperty(name, value) {
    this.values.set(name, String(value));
  }
}

class FakeClassList {
  constructor(owner, initial = []) {
    this.owner = owner;
    this.values = new Set(initial);
    this.sync();
  }

  reset(value) {
    this.values = new Set(String(value || "").split(/\s+/).filter(Boolean));
    this.sync();
  }

  sync() {
    this.owner._className = [...this.values].join(" ");
  }

  contains(name) {
    return this.values.has(name);
  }

  add(...names) {
    for (const name of names) this.values.add(name);
    this.sync();
  }

  remove(...names) {
    for (const name of names) this.values.delete(name);
    this.sync();
  }

  toggle(name, force) {
    const enabled = force === undefined ? !this.values.has(name) : !!force;
    if (enabled) this.values.add(name);
    else this.values.delete(name);
    this.sync();
    return enabled;
  }
}

function dataPropertyName(attributeName) {
  return attributeName.slice(5).replace(/-([a-z])/g, (_, letter) => letter.toUpperCase());
}

class FakeElement extends EventHub {
  constructor(tagName = "div", options = {}) {
    super();
    this.nodeType = 1;
    this.tagName = String(tagName).toUpperCase();
    this.id = options.id || "";
    this._className = "";
    this.classList = new FakeClassList(this, options.classes || []);
    this.attributes = new Map();
    this.dataset = {};
    this.style = new FakeStyle();
    this.children = [];
    this.parentNode = null;
    this.hidden = false;
    this.disabled = false;
    this.title = "";
    this.value = options.value === undefined ? "" : String(options.value);
    this.textContent = options.textContent || "";
    this._innerHTML = "";
    this._connected = false;
    this.clickCount = 0;
    this.pointerCaptureId = null;

    for (const [name, value] of Object.entries(options.attributes || {})) {
      this.setAttribute(name, value);
    }
  }

  get className() {
    return this._className;
  }

  set className(value) {
    this.classList.reset(value);
  }

  get innerHTML() {
    return this._innerHTML;
  }

  set innerHTML(value) {
    this._innerHTML = String(value);
  }

  get isConnected() {
    return this._connected;
  }

  connect() {
    this._connected = true;
    for (const child of this.children) child.connect();
  }

  disconnect() {
    this._connected = false;
    for (const child of this.children) child.disconnect();
  }

  appendChild(child) {
    child.parentNode = this;
    this.children.push(child);
    if (this.isConnected) child.connect();
    return child;
  }

  removeChild(child) {
    this.children = this.children.filter(candidate => candidate !== child);
    child.parentNode = null;
    child.disconnect();
    return child;
  }

  contains(candidate) {
    if (candidate === this) return true;
    return this.children.some(child => child.contains(candidate));
  }

  setAttribute(name, value) {
    const text = String(value);
    this.attributes.set(name, text);
    if (name === "id") this.id = text;
    else if (name === "class") this.className = text;
    else if (name.startsWith("data-")) this.dataset[dataPropertyName(name)] = text;
  }

  getAttribute(name) {
    if (name === "id") return this.id || null;
    if (name === "class") return this.className || null;
    return this.attributes.has(name) ? this.attributes.get(name) : null;
  }

  matches(selectorList) {
    return String(selectorList).split(",").some(selector => this.matchesOne(selector.trim()));
  }

  matchesOne(selector) {
    if (!selector || selector === ":active" || selector.endsWith(":hover")) return false;

    const notAttribute = selector.match(/^(.*):not\(\[([^=]+)=['"]([^'"]+)['"]\]\)$/);
    if (notAttribute) {
      return this.matchesOne(notAttribute[1]) && this.getAttribute(notAttribute[2]) !== notAttribute[3];
    }

    const attribute = selector.match(/^\[([^=\]]+)(?:=['"]?([^'"\]]+)['"]?)?\]$/);
    if (attribute) {
      const current = this.getAttribute(attribute[1]);
      return attribute[2] === undefined ? current !== null : current === attribute[2];
    }

    if (selector.startsWith("#")) return this.id === selector.slice(1);
    if (selector.startsWith(".")) return this.classList.contains(selector.slice(1));

    const tagAndClass = selector.match(/^([a-z0-9-]+)\.([a-z0-9_-]+)$/i);
    if (tagAndClass) {
      return this.tagName === tagAndClass[1].toUpperCase() && this.classList.contains(tagAndClass[2]);
    }

    return /^[a-z0-9-]+$/i.test(selector) && this.tagName === selector.toUpperCase();
  }

  closest(selectorList) {
    for (let current = this; current; current = current.parentNode) {
      if (current.matches(selectorList)) return current;
    }
    return null;
  }

  querySelector(selector) {
    return querySelectorWithin(this, selector);
  }

  hasPointerCapture(pointerId) {
    return this.pointerCaptureId === pointerId;
  }

  releasePointerCapture(pointerId) {
    if (this.pointerCaptureId === pointerId) this.pointerCaptureId = null;
  }

  click() {
    this.clickCount++;
  }
}

class FakeMediaElement extends FakeElement {
  constructor() {
    super("video", { classes: ["html5-main-video"] });
    this.paused = true;
    this.muted = false;
    this.volume = 1;
    this.duration = 120;
    this._currentTime = 12;
    this.currentTimeWrites = 0;
    this._playbackRate = 1;
    this.playbackRateWrites = 0;
    this.playCount = 0;
    this.pauseCount = 0;
  }

  get currentTime() {
    return this._currentTime;
  }

  set currentTime(value) {
    this.currentTimeWrites++;
    this._currentTime = Number(value);
  }

  get playbackRate() {
    return this._playbackRate;
  }

  set playbackRate(value) {
    this.playbackRateWrites++;
    this._playbackRate = Number(value);
  }

  play() {
    this.playCount++;
    this.paused = false;
    this.emit("play", { isTrusted: true, target: this });
    return { catch() {} };
  }

  pause() {
    this.pauseCount++;
    this.paused = true;
    this.emit("pause", { isTrusted: true, target: this });
  }
}

function descendants(root) {
  const result = [];
  for (const child of root.children) {
    result.push(child, ...descendants(child));
  }
  return result;
}

function querySelectorWithin(root, selectorList) {
  const selectors = String(selectorList).split(",").map(selector => selector.trim());
  for (const element of descendants(root)) {
    if (selectors.some(selector => element.matchesOne(selector))) return element;
  }
  return null;
}

class FakeDocument extends EventHub {
  constructor({ includePlayer = true, includeFocusedRoot = false, adClass = null } = {}) {
    super();
    this.readyState = "complete";
    this.activeElement = null;
    this.fullscreenElement = null;
    this.pointerLockElement = null;
    this.documentElement = new FakeElement("html");
    this.head = new FakeElement("head");
    this.body = new FakeElement("body");
    this.documentElement.appendChild(this.head);
    this.documentElement.appendChild(this.body);
    this.documentElement.connect();

    this.player = null;
    this.media = null;
    this.nativeCaptions = null;
    this.nativeNext = null;
    this.nativeAdControl = null;
    this.focused = null;

    if (includePlayer) {
      this.player = new FakeElement("div", { id: "movie_player", classes: ["html5-video-player"] });
      if (adClass) this.player.classList.add(adClass);
      this.media = new FakeMediaElement();
      this.nativeCaptions = new FakeElement("button", { classes: ["ytp-subtitles-button"] });
      this.nativeCaptions.setAttribute("aria-pressed", "false");
      this.nativeNext = new FakeElement("button", { classes: ["ytp-next-button"] });
      this.nativeNext.setAttribute("aria-disabled", "false");
      this.nativeAdControl = new FakeElement("button", { classes: ["ytp-ad-skip-button"] });
      this.player.appendChild(this.media);
      this.player.appendChild(this.nativeCaptions);
      this.player.appendChild(this.nativeNext);
      this.player.appendChild(this.nativeAdControl);
      this.body.appendChild(this.player);
    }

    if (includeFocusedRoot) {
      this.focused = buildFocusedRoot();
      this.body.appendChild(this.focused.root);
    }
  }

  createElement(tagName) {
    return new FakeElement(tagName);
  }

  getElementById(id) {
    if (this.documentElement.id === id) return this.documentElement;
    return descendants(this.documentElement).find(element => element.id === id) || null;
  }

  contains(element) {
    return this.documentElement.contains(element);
  }

  querySelector(selector) {
    if (selector === "#movie_player,.html5-video-player")
      return this.player && this.player.isConnected ? this.player : null;
    if (selector === "#movie_player video.html5-main-video,video.html5-main-video,video") return this.media;
    if (selector === ".ytp-subtitles-button") return this.nativeCaptions;
    if (selector === ".ytp-next-button") return this.nativeNext;
    if (selector === ".ytp-next-button:not([aria-disabled='true'])") {
      return this.nativeNext && this.nativeNext.getAttribute("aria-disabled") !== "true"
        ? this.nativeNext
        : null;
    }
    return querySelectorWithin(this.documentElement, selector);
  }
}

class FakeMutationObserver {
  constructor(callback) {
    this.callback = callback;
    this.connected = false;
  }

  observe() {
    this.connected = true;
  }

  disconnect() {
    this.connected = false;
  }
}

function buildFocusedRoot() {
  const root = new FakeElement("div", {
    id: "piplay-focused-overlay",
    classes: ["piplay-focused-overlay", "is-visible"],
    attributes: { "data-piplay-no-drag": "true" },
  });
  const controls = {};
  const buttonActions = ["mute", "captions", "settings", "pinToggle", "fullscreenToggle", "close", "playPause", "next"];
  for (const action of buttonActions) {
    const button = new FakeElement("button", {
      classes: ["piplay-focused-button"],
      attributes: { "data-action": action, "data-piplay-no-drag": "true" },
    });
    root.appendChild(button);
    controls[action] = button;
  }

  controls.seek = new FakeElement("input", {
    classes: ["piplay-focused-progress"],
    attributes: { "data-action": "seek", "data-piplay-no-drag": "true" },
    value: "0",
  });
  controls.current = new FakeElement("span", { attributes: { "data-time": "current" } });
  controls.duration = new FakeElement("span", { attributes: { "data-time": "duration" } });
  root.appendChild(controls.current);
  root.appendChild(controls.seek);
  root.appendChild(controls.duration);
  return { root, controls };
}

function createEnvironment(options = {}) {
  const document = new FakeDocument(options);
  const window = new EventHub();
  const messages = [];
  let timerId = 0;
  const timers = new Map();

  window.top = options.topLevel === false ? {} : window;
  window.document = document;
  window.location = {
    hostname: options.hostname || "www.youtube.com",
    pathname: options.pathname || "/watch",
  };
  window.chrome = {
    webview: {
      postMessage(message) {
        messages.push(String(message));
      },
    },
  };
  window.setTimeout = (callback, delay) => {
    const id = ++timerId;
    timers.set(id, { callback, delay, interval: false });
    return id;
  };
  window.clearTimeout = id => timers.delete(id);
  window.setInterval = (callback, delay) => {
    const id = ++timerId;
    timers.set(id, { callback, delay, interval: true });
    return id;
  };
  window.clearInterval = id => timers.delete(id);

  const sandbox = {
    window,
    document,
    location: window.location,
    Node: { ELEMENT_NODE: 1 },
    MutationObserver: FakeMutationObserver,
    performance: { now: () => 1000 },
    console,
  };
  const context = vm.createContext(sandbox);
  return { context, window, document, messages, timers };
}

function execute(environment, script) {
  return vm.runInContext(script, environment.context, { timeout: 1000 });
}

function pointerEvent(target, player, overrides = {}) {
  const event = {
    isTrusted: true,
    isPrimary: true,
    button: 0,
    buttons: 1,
    pointerType: "mouse",
    pointerId: 7,
    clientX: 0,
    clientY: 0,
    target,
    defaultPrevented: false,
    immediatePropagationStopped: false,
    composedPath: () => player ? [target, player] : [target],
    preventDefault() { this.defaultPrevented = true; },
    stopImmediatePropagation() { this.immediatePropagationStopped = true; },
  };
  return Object.assign(event, overrides);
}

function actionEvent(target, trusted) {
  return {
    isTrusted: trusted,
    target,
    defaultPrevented: false,
    preventDefault() { this.defaultPrevented = true; },
    stopImmediatePropagation() {},
  };
}

function authorizePassive(environment) {
  equal(execute(environment, input.passiveAuthorizeScript), true,
    "passive document authorization script must return true");
}

function authorizeFocused(environment) {
  equal(execute(environment, input.focusedAuthorizeScript), true,
    "Focused document authorization script must return true");
  equal(execute(environment, input.focusedStateRequestScript), true,
    "Focused state request script must return true");
}

function adPostures() {
  const stale = createEnvironment();
  stale.document.documentElement.removeChild(stale.document.body);

  const missingPlayer = createEnvironment({ includePlayer: false });
  missingPlayer.document.media = new FakeMediaElement();
  missingPlayer.document.body.appendChild(missingPlayer.document.media);

  return [
    { name: "clear", environment: createEnvironment(), probe: "clear", seeks: true, videoReachable: true },
    { name: "ad-showing", environment: createEnvironment({ adClass: "ad-showing" }), probe: "ad", seeks: false, videoReachable: true },
    { name: "ad-interrupting", environment: createEnvironment({ adClass: "ad-interrupting" }), probe: "ad", seeks: false, videoReachable: true },
    { name: "missing player", environment: missingPlayer, probe: "unknown", seeks: false, videoReachable: true },
    { name: "stale document", environment: stale, probe: "unknown", seeks: false, videoReachable: false },
  ];
}

scenario("passive drag preserves clicks until one trusted threshold crossing", () => {
  const revoked = createEnvironment();
  execute(revoked, input.passiveScript);
  const revokedDown = pointerEvent(revoked.document.media, revoked.document.player);
  const revokedMove = pointerEvent(revoked.document.media, revoked.document.player, { clientX: 10 });
  revoked.window.emit("pointerdown", revokedDown);
  revoked.window.emit("pointermove", revokedMove);
  equal(revoked.messages.length, 0, "revoked passive script must not post");

  const environment = createEnvironment();
  execute(environment, input.passiveScript);
  authorizePassive(environment);
  const target = environment.document.media;
  target.pointerCaptureId = 7;

  const down = pointerEvent(target, environment.document.player);
  const below = pointerEvent(target, environment.document.player, { clientX: 3, clientY: 3 });
  environment.window.emit("pointerdown", down);
  environment.window.emit("pointermove", below);
  equal(environment.messages.length, 0, "below-threshold movement must not post");
  equal(below.defaultPrevented, false, "below-threshold movement must remain untouched");
  environment.window.emit("pointerup", pointerEvent(target, environment.document.player));
  const ordinaryClick = actionEvent(target, true);
  environment.window.emit("click", ordinaryClick);
  equal(ordinaryClick.defaultPrevented, false, "ordinary click must not be suppressed");

  const crossing = pointerEvent(target, environment.document.player, { clientX: 4 });
  environment.window.emit("pointerdown", pointerEvent(target, environment.document.player));
  environment.window.emit("pointermove", crossing);
  environment.window.emit("pointermove", pointerEvent(target, environment.document.player, { clientX: 20 }));
  equal(environment.messages.length, 1, "threshold gesture must post exactly once");
  assert(crossing.defaultPrevented && crossing.immediatePropagationStopped,
    "threshold-crossing move must be suppressed before native handoff");
  equal(target.pointerCaptureId, null, "pointer capture must be released before native handoff");

  const message = JSON.parse(environment.messages[0]);
  equal(message.channel, "piplay.window", "drag channel");
  equal(message.type, "dragStart", "drag message type");
  equal(message.nonce, input.nonce, "drag nonce");
  equal(message.documentToken, input.documentToken, "drag document token");
  assert(!Object.hasOwn(message, "clientX") && !Object.hasOwn(message, "clientY"),
    "drag payload must not expose pointer coordinates");

  const suppressedClick = actionEvent(target, true);
  environment.window.emit("click", suppressedClick);
  equal(suppressedClick.defaultPrevented, true, "only the completed drag click must be suppressed");
  const followingClick = actionEvent(target, true);
  environment.window.emit("click", followingClick);
  equal(followingClick.defaultPrevented, false, "click suppression must clear after one click");
});

scenario("passive drag rejects synthetic, touch, non-primary, child-frame, and non-player gestures", () => {
  const environment = createEnvironment();
  execute(environment, input.passiveScript);
  authorizePassive(environment);
  const target = environment.document.media;
  const player = environment.document.player;

  const rejectedDowns = [
    { isTrusted: false },
    { pointerType: "touch" },
    { isPrimary: false },
    { button: 1 },
  ];
  for (const override of rejectedDowns) {
    environment.window.emit("pointerdown", pointerEvent(target, player, override));
    environment.window.emit("pointermove", pointerEvent(target, player, { clientX: 20 }));
    environment.window.emit("pointerup", pointerEvent(target, player));
  }

  environment.window.emit("pointerdown", pointerEvent(target, player));
  environment.window.emit("pointermove", pointerEvent(target, player, { isTrusted: false, clientX: 20 }));
  environment.window.emit("pointerup", pointerEvent(target, player));

  const outside = new FakeElement("div");
  environment.document.body.appendChild(outside);
  environment.window.emit("pointerdown", pointerEvent(outside, null));
  environment.window.emit("pointermove", pointerEvent(outside, null, { clientX: 20 }));
  equal(environment.messages.length, 0, "rejected passive gestures must not post");

  const child = createEnvironment({ topLevel: false });
  execute(child, input.passiveScript);
  equal(child.window.listenerCount("pointerdown"), 0, "child frame must not install drag listeners");
  equal(child.window.__piplaySurfaceDragInstalled, undefined, "child frame must not mark drag installed");
});

scenario("passive drag excludes every declared interactive YouTube and PiPlay surface", () => {
  const environment = createEnvironment();
  execute(environment, input.passiveScript);
  authorizePassive(environment);
  const player = environment.document.player;
  const excludedTargets = [
    new FakeElement("a"),
    new FakeElement("button"),
    new FakeElement("input"),
    new FakeElement("select"),
    new FakeElement("textarea"),
    new FakeElement("div", { attributes: { role: "button" } }),
    new FakeElement("div", { attributes: { role: "slider" } }),
    new FakeElement("div", { attributes: { role: "menuitem" } }),
    new FakeElement("div", { attributes: { contenteditable: "true" } }),
    new FakeElement("div", { attributes: { "data-piplay-no-drag": "true" } }),
    ...[
      "piplay-focused-overlay", "ytp-progress-bar-container", "ytp-volume-area",
      "ytp-subtitles-button", "ytp-settings-button", "ytp-fullscreen-button",
      "ytp-popup", "ytp-settings-menu", "ytp-menuitem", "ytp-ce-element",
      "ytp-endscreen-content", "ytp-cards-button", "ytp-cards-teaser",
      "ytp-caption-window-container", "ytp-ad-overlay-container", "ytp-ad-player-overlay",
    ].map(className => new FakeElement("div", { classes: [className] })),
  ];

  for (const target of excludedTargets) {
    player.appendChild(target);
    environment.window.emit("pointerdown", pointerEvent(target, player));
    environment.window.emit("pointermove", pointerEvent(target, player, { clientX: 20 }));
    environment.window.emit("pointerup", pointerEvent(target, player));
  }
  equal(environment.messages.length, 0, "interactive targets must never arm passive dragging");
});

scenario("passive drag document-token rotation clears an armed stale gesture", () => {
  const environment = createEnvironment();
  execute(environment, input.passiveScript);
  authorizePassive(environment);
  const target = environment.document.media;
  const player = environment.document.player;

  environment.window.emit("pointerdown", pointerEvent(target, player));
  equal(execute(environment, input.passiveReauthorizeScript), true,
    "replacement passive authorization must return true");
  environment.window.emit("pointermove", pointerEvent(target, player, { clientX: 20 }));
  equal(environment.messages.length, 0, "token rotation must clear an armed old-document gesture");

  environment.window.emit("pointerdown", pointerEvent(target, player));
  environment.window.emit("pointermove", pointerEvent(target, player, { clientX: 20 }));
  equal(environment.messages.length, 1, "new document gesture must post");
  equal(JSON.parse(environment.messages[0]).documentToken, input.replacementDocumentToken,
    "new document gesture must carry only the replacement token");
});

scenario("Focused surface rejects synthetic media and native actions", () => {
  const environment = createEnvironment({ includeFocusedRoot: true });
  execute(environment, input.focusedScript);
  authorizeFocused(environment);
  environment.messages.length = 0;
  const { controls, root } = environment.document.focused;
  const media = environment.document.media;
  const initialTime = media.currentTime;

  for (const action of ["close", "pinToggle", "fullscreenToggle", "settings", "playPause", "mute", "captions", "next"]) {
    root.emit("click", actionEvent(controls[action], false));
  }
  controls.seek.value = "900";
  root.emit("input", actionEvent(controls.seek, false));

  equal(environment.messages.length, 0, "synthetic Focused controls must not post native actions");
  equal(media.playCount, 0, "synthetic Play must not run");
  equal(media.pauseCount, 0, "synthetic Pause must not run");
  equal(media.muted, false, "synthetic Mute must not run");
  equal(media.currentTime, initialTime, "synthetic seek must not change time");
  equal(media.currentTimeWrites, 0, "synthetic seek must not write currentTime");
  equal(environment.document.nativeCaptions.clickCount, 0, "synthetic captions must not click native control");
  equal(environment.document.nativeNext.clickCount, 0, "synthetic Next must not click native control");
});

scenario("Focused surface blocks seek and Next for every recognized ad state", () => {
  for (const adClass of ["ad-showing", "ad-interrupting"]) {
    const environment = createEnvironment({ includeFocusedRoot: true, adClass });
    execute(environment, input.focusedScript);
    authorizeFocused(environment);
    environment.messages.length = 0;
    const { controls, root } = environment.document.focused;
    const media = environment.document.media;
    const initialTime = media.currentTime;

    assert(root.classList.contains("is-ad"), `${adClass} must place the overlay in ad posture`);
    equal(controls.seek.disabled, true, `${adClass} must disable custom seek`);
    equal(controls.next.disabled, true, `${adClass} must disable custom Next`);

    controls.seek.value = "900";
    root.emit("input", actionEvent(controls.seek, true));
    root.emit("click", actionEvent(controls.next, true));
    equal(media.currentTime, initialTime, `${adClass} must preserve ad playback position`);
    equal(media.currentTimeWrites, 0, `${adClass} must never write currentTime`);
    equal(environment.document.nativeNext.clickCount, 0, `${adClass} must never invoke native Next`);
    assert(environment.document.nativeAdControl.isConnected && !environment.document.nativeAdControl.hidden,
      `${adClass} must leave native ad controls connected and visible`);
    equal(environment.messages.length, 0, `${adClass} media actions must not widen the native bridge`);
  }
});

scenario("Focused non-ad trusted branches work and emit only the closed native action", () => {
  const environment = createEnvironment({ includeFocusedRoot: true });
  execute(environment, input.focusedScript);
  authorizeFocused(environment);
  environment.messages.length = 0;
  const { controls, root } = environment.document.focused;
  const media = environment.document.media;

  controls.seek.value = "750";
  root.emit("input", actionEvent(controls.seek, true));
  equal(media.currentTime, 90, "trusted non-ad seek must update playback position");
  equal(media.currentTimeWrites, 1, "trusted non-ad seek must perform one currentTime write");
  root.emit("click", actionEvent(controls.next, true));
  equal(environment.document.nativeNext.clickCount, 1, "trusted non-ad Next must use the native control");

  root.emit("click", actionEvent(controls.close, true));
  equal(environment.messages.length, 1, "trusted Close must post exactly one native request");
  const message = JSON.parse(environment.messages[0]);
  equal(message.channel, "piplay.focused", "Focused native channel");
  equal(message.type, "request", "Focused native message type");
  equal(message.action, "close", "Focused native action");
  equal(message.nonce, input.nonce, "Focused native nonce");
  equal(message.documentToken, input.documentToken, "Focused native document token");
  equal(Object.keys(message).sort().join(","), "action,channel,documentToken,nonce,type,v",
    "Focused request must retain its exact closed schema");
});

scenario("Focused selector failure withdraws harmlessly and reports inactive", () => {
  const environment = createEnvironment({ includePlayer: false, includeFocusedRoot: false });
  execute(environment, input.focusedScript);
  authorizeFocused(environment);

  assert(!environment.document.documentElement.classList.contains("piplay-focused-active"),
    "missing player must leave the ordinary page inactive");
  equal(environment.messages.length, 1, "missing player must produce one requested state handshake");
  const message = JSON.parse(environment.messages[0]);
  equal(message.type, "state", "selector failure handshake type");
  equal(message.active, false, "selector failure must report inactive");
  equal([...environment.timers.values()].filter(timer => timer.interval).length, 0,
    "selector failure must not leave the active fallback interval running");
});

scenario("ad-state probe classifies clear, both ad classes, missing player, and stale documents", () => {
  for (const posture of adPostures()) {
    equal(execute(posture.environment, input.adStateProbeScript), posture.probe,
      `${posture.name} must probe as ${posture.probe}`);
  }
});

scenario("host seek writer applies currentTime only in the clear posture", () => {
  for (const posture of adPostures()) {
    const media = posture.environment.document.media;
    const initialTime = media.currentTime;
    execute(posture.environment, input.seekScript);
    equal(media.currentTimeWrites, posture.seeks ? 1 : 0,
      `${posture.name} seek currentTime writes`);
    equal(media.currentTime, posture.seeks ? 95 : initialTime,
      `${posture.name} seek final position`);
  }
});

scenario("host seek-and-pause always pauses but applies currentTime only when clear", () => {
  for (const posture of adPostures()) {
    const media = posture.environment.document.media;
    const initialTime = media.currentTime;
    execute(posture.environment, input.seekAndPauseScript);
    equal(media.currentTimeWrites, posture.seeks ? 1 : 0,
      `${posture.name} seek-and-pause currentTime writes`);
    equal(media.currentTime, posture.seeks ? 95 : initialTime,
      `${posture.name} seek-and-pause final position`);
    equal(media.pauseCount, posture.videoReachable ? 1 : 0,
      `${posture.name} seek-and-pause must pause every reachable video`);
  }
});

scenario("host seek-and-play always plays but applies currentTime only when clear", () => {
  for (const posture of adPostures()) {
    const media = posture.environment.document.media;
    const initialTime = media.currentTime;
    execute(posture.environment, input.seekAndPlayScript);
    equal(media.currentTimeWrites, posture.seeks ? 1 : 0,
      `${posture.name} seek-and-play currentTime writes`);
    equal(media.currentTime, posture.seeks ? 95 : initialTime,
      `${posture.name} seek-and-play final position`);
    equal(media.playCount, posture.videoReachable ? 1 : 0,
      `${posture.name} seek-and-play must play every reachable video`);
    equal(media.paused, !posture.videoReachable,
      `${posture.name} seek-and-play playing state`);
  }
});

scenario("host playback settings always set volume and mute but playbackRate only when clear", () => {
  for (const posture of adPostures()) {
    const media = posture.environment.document.media;
    execute(posture.environment, input.playbackSettingsScript);
    equal(media.volume, posture.videoReachable ? 0.4 : 1,
      `${posture.name} playback-settings volume`);
    equal(media.muted, posture.videoReachable,
      `${posture.name} playback-settings mute`);
    equal(media.playbackRateWrites, posture.seeks ? 1 : 0,
      `${posture.name} playback-settings playbackRate writes`);
    equal(media.playbackRate, posture.seeks ? 1.75 : 1,
      `${posture.name} playback-settings final playbackRate`);
  }
});

if (failures.length) {
  process.stderr.write(`\n${failures.join("\n\n")}\n`);
  process.exitCode = 1;
} else {
  process.stdout.write(`DOM HARNESS PASS (${passed} scenarios)\n`);
}

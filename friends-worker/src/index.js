import { DurableObject } from "cloudflare:workers";

const ROSTER = [
  "gugomot",
  "wwzmx",
  "wlimon4ik",
  "batton111221",
  "syn_mijaghi",
  "IMPERATOR_MIDASS"
];

const ALIAS_MAP = {
  maksim72622: "batton111221",
  persic760gmail_c: "batton111221",
  batton: "batton111221",
  batun: "batton111221",
  wlimonchik: "wlimon4ik",
  imperator_midas: "IMPERATOR_MIDASS"
};

const DEFAULT_CLIENT_KEY = "civ-friends-26-3-k9m4x8q2";
const ONLINE_TTL_MS = 30000;
const SQL_SEEN_FLUSH_MS = 60000;
const MAX_SKIN_B64_LEN = 240000;
const MAX_CHAT_HISTORY_PER_PAIR = 300;

function normalizeNick(raw) {
  if (typeof raw !== "string") return null;
  const trimmed = raw.trim();
  if (!trimmed) return null;
  const lower = trimmed.toLowerCase();
  for (let i = 0; i < ROSTER.length; i++) {
    if (ROSTER[i].toLowerCase() === lower) return ROSTER[i];
  }
  if (Object.prototype.hasOwnProperty.call(ALIAS_MAP, lower)) {
    return ALIAS_MAP[lower];
  }
  return null;
}

function makePairKey(a, b) {
  return a.toLowerCase() < b.toLowerCase() ? a + "|" + b : b + "|" + a;
}

function formatOfflineDuration(diffMs) {
  if (!Number.isFinite(diffMs) || diffMs <= 0) return "Не в сети";
  const mins = Math.floor(diffMs / 60000);
  if (mins < 2) return "Был в сети только что";
  if (mins < 60) return "Был в сети " + mins + " мин назад";
  const hours = Math.floor(mins / 60);
  if (hours < 24) return "Был в сети " + hours + "ч назад";
  const days = Math.floor(hours / 24);
  if (days === 1) return "Был в сети вчера";
  const mod10 = days % 10;
  const mod100 = days % 100;
  let dayWord = "дней";
  if (mod100 < 11 || mod100 > 19) {
    if (mod10 === 1) dayWord = "день";
    else if (mod10 >= 2 && mod10 <= 4) dayWord = "дня";
  }
  return "Был в сети " + days + " " + dayWord + " назад";
}

function jsonResponse(obj, status = 200) {
  return new Response(JSON.stringify(obj), {
    status,
    headers: {
      "Content-Type": "application/json; charset=utf-8",
      "Cache-Control": "no-store",
      "Access-Control-Allow-Origin": "*"
    }
  });
}

function base64ToBytes(b64) {
  const bin = atob(b64);
  const bytes = new Uint8Array(bin.length);
  for (let i = 0; i < bin.length; i++) {
    bytes[i] = bin.charCodeAt(i);
  }
  return bytes;
}

export class FriendsHub extends DurableObject {
  constructor(ctx, env) {
    super(ctx, env);
    this.sql = ctx.storage.sql;
    this.live = new Map();
    this.persistedSeenMs = new Map();
    this.initialized = false;

    this.ctx.blockConcurrencyWhile(async () => {
      this.ensureInit();
    });
  }

  ensureInit() {
    if (this.initialized) return;

    this.sql.exec(`
      CREATE TABLE IF NOT EXISTS friends_meta (
        nick TEXT PRIMARY KEY,
        last_seen_ms INTEGER NOT NULL DEFAULT 0,
        status TEXT NOT NULL DEFAULT 'offline',
        activity TEXT NOT NULL DEFAULT '',
        skin_hash TEXT NOT NULL DEFAULT '',
        skin_b64 TEXT NOT NULL DEFAULT '',
        skin_model TEXT NOT NULL DEFAULT 'classic'
      );
      CREATE TABLE IF NOT EXISTS messages (
        id INTEGER PRIMARY KEY AUTOINCREMENT,
        pair_key TEXT NOT NULL,
        sender TEXT NOT NULL,
        recipient TEXT NOT NULL,
        text TEXT NOT NULL,
        ts_ms INTEGER NOT NULL
      );
      CREATE INDEX IF NOT EXISTS idx_messages_pair_id ON messages(pair_key, id);
      CREATE INDEX IF NOT EXISTS idx_messages_recipient ON messages(recipient, sender, id);
      CREATE TABLE IF NOT EXISTS read_state (
        reader TEXT NOT NULL,
        peer TEXT NOT NULL,
        last_read_id INTEGER NOT NULL DEFAULT 0,
        PRIMARY KEY (reader, peer)
      );
    `);

    for (let i = 0; i < ROSTER.length; i++) {
      this.sql.exec("INSERT OR IGNORE INTO friends_meta (nick) VALUES (?)", ROSTER[i]);
    }

    const rows = this.sql.exec(
      "SELECT nick, last_seen_ms, status, activity, skin_hash, skin_model FROM friends_meta"
    ).toArray();

    for (let i = 0; i < rows.length; i++) {
      const r = rows[i];
      const canonical = normalizeNick(r.nick);
      if (!canonical) continue;
      const seenMs = Number(r.last_seen_ms || 0);
      this.live.set(canonical, {
        nick: canonical,
        lastSeenMs: seenMs,
        status: r.status || "offline",
        activity: r.activity || "",
        skinHash: r.skin_hash || "",
        skinModel: r.skin_model || "classic"
      });
      this.persistedSeenMs.set(canonical, seenMs);
    }

    for (let i = 0; i < ROSTER.length; i++) {
      const nick = ROSTER[i];
      if (!this.live.has(nick)) {
        this.live.set(nick, {
          nick,
          lastSeenMs: 0,
          status: "offline",
          activity: "",
          skinHash: "",
          skinModel: "classic"
        });
        this.persistedSeenMs.set(nick, 0);
      }
    }

    this.initialized = true;
  }

  async fetch(request) {
    this.ensureInit();
    const url = new URL(request.url);
    const path = url.pathname;

    if (request.method === "POST" && path === "/friends/sync") {
      return this.handleSync(request);
    }
    if (request.method === "GET" && path === "/friends/state") {
      return this.handleGetState(url.searchParams.get("nick"));
    }
    if (request.method === "GET" && path === "/friends/skin") {
      return this.handleGetSkin(url.searchParams.get("nick"));
    }
    if (request.method === "GET" && path === "/friends/chat") {
      return this.handleGetChat(url);
    }
    if (request.method === "POST" && path === "/friends/chat") {
      return this.handlePostChat(request);
    }
    if (request.method === "GET" && path === "/health") {
      return jsonResponse({ ok: true, rosterCount: ROSTER.length, serverNowMs: Date.now() });
    }
    return jsonResponse({ ok: false, error: "Not found" }, 404);
  }

  async handleSync(request) {
    let body;
    try {
      body = await request.json();
    } catch {
      return jsonResponse({ ok: false, error: "Invalid JSON" }, 400);
    }

    const me = normalizeNick(body && body.nick);
    if (!me) {
      return jsonResponse({ ok: false, error: "Unknown player" }, 403);
    }

    const now = Date.now();
    const rawStatus = typeof body.status === "string" ? body.status.trim().toLowerCase() : "launcher";
    const status = rawStatus === "ingame" ? "ingame" : "launcher";
    const activity =
      status === "ingame"
        ? "Играет в Civilization"
        : "В лаунчере";

    const skinHash = typeof body.skinHash === "string" ? body.skinHash.trim().slice(0, 64) : "";
    const skinModel = body.skinModel === "slim" ? "slim" : "classic";
    const skinBase64 = typeof body.skinBase64 === "string" ? body.skinBase64.trim() : "";

    const cur = this.live.get(me);
    const statusChanged = cur.status !== status || cur.activity !== activity;
    const hashChanged = cur.skinHash !== skinHash || cur.skinModel !== skinModel;

    cur.lastSeenMs = now;
    cur.status = status;
    cur.activity = activity;

    let shouldFlushSkin = false;
    let newSkinB64 = null;

    if (skinHash === "" && cur.skinHash !== "") {
      cur.skinHash = "";
      cur.skinModel = "classic";
      newSkinB64 = "";
      shouldFlushSkin = true;
    } else if (skinHash && skinBase64 && skinBase64.length <= MAX_SKIN_B64_LEN) {
      cur.skinHash = skinHash;
      cur.skinModel = skinModel;
      newSkinB64 = skinBase64;
      shouldFlushSkin = true;
    } else if (skinHash && hashChanged) {
      // Клиент прислал новый hash без payload только если сбросили, иначе ждём skinBase64
      if (!cur.skinHash) {
        cur.skinModel = skinModel;
      }
    }

    const lastPersisted = this.persistedSeenMs.get(me) || 0;
    if (shouldFlushSkin) {
      this.sql.exec(
        "UPDATE friends_meta SET last_seen_ms = ?, status = ?, activity = ?, skin_hash = ?, skin_b64 = ?, skin_model = ? WHERE nick = ?",
        now,
        status,
        activity,
        cur.skinHash,
        newSkinB64,
        cur.skinModel,
        me
      );
      this.persistedSeenMs.set(me, now);
    } else if (statusChanged || now - lastPersisted >= SQL_SEEN_FLUSH_MS) {
      this.sql.exec(
        "UPDATE friends_meta SET last_seen_ms = ?, status = ?, activity = ? WHERE nick = ?",
        now,
        status,
        activity,
        me
      );
      this.persistedSeenMs.set(me, now);
    }

    return this.buildStateResponse(me, now);
  }

  handleGetState(rawNick) {
    const me = normalizeNick(rawNick);
    if (!me) {
      return jsonResponse({ ok: false, error: "Unknown player" }, 403);
    }
    return this.buildStateResponse(me, Date.now());
  }

  buildStateResponse(me, now) {
    const unreadRows = this.sql.exec(
      `SELECT m.sender AS peer, COUNT(*) AS cnt, MAX(m.id) AS last_id
       FROM messages m
       LEFT JOIN read_state r ON r.reader = ? AND r.peer = m.sender
       WHERE m.recipient = ? AND m.id > COALESCE(r.last_read_id, 0)
       GROUP BY m.sender`,
      me,
      me
    ).toArray();

    const unreadMap = new Map();
    for (let i = 0; i < unreadRows.length; i++) {
      unreadMap.set(unreadRows[i].peer, Number(unreadRows[i].cnt || 0));
    }

    const latestRows = this.sql.exec(
      `SELECT pair_key, MAX(id) AS max_id FROM messages WHERE sender = ? OR recipient = ? GROUP BY pair_key`,
      me,
      me
    ).toArray();

    const lastIdMap = new Map();
    for (let i = 0; i < latestRows.length; i++) {
      lastIdMap.set(latestRows[i].pair_key, Number(latestRows[i].max_id || 0));
    }

    const friends = [];
    for (let i = 0; i < ROSTER.length; i++) {
      const nick = ROSTER[i];
      if (nick === me) continue;

      const entry = this.live.get(nick);
      const seenMs = entry ? entry.lastSeenMs : 0;
      const isOnline = seenMs > 0 && now - seenMs <= ONLINE_TTL_MS;
      const effStatus = isOnline ? (entry.status === "ingame" ? "ingame" : "launcher") : "offline";
      const effActivity = isOnline
        ? entry.activity || (effStatus === "ingame" ? "Играет в Civilization" : "В лаунчере")
        : seenMs > 0
          ? formatOfflineDuration(now - seenMs)
          : "Не в сети";

      const pairKey = makePairKey(me, nick);
      friends.push({
        nick,
        status: effStatus,
        activity: effActivity,
        lastSeenMs: seenMs,
        skinHash: entry ? entry.skinHash : "",
        skinModel: entry ? entry.skinModel : "classic",
        unread: unreadMap.get(nick) || 0,
        lastMsgId: lastIdMap.get(pairKey) || 0
      });
    }

    const rank = { ingame: 0, launcher: 1, offline: 2 };
    friends.sort((a, b) => {
      const ra = rank[a.status] !== undefined ? rank[a.status] : 9;
      const rb = rank[b.status] !== undefined ? rank[b.status] : 9;
      if (ra !== rb) return ra - rb;
      if (b.lastSeenMs !== a.lastSeenMs) return b.lastSeenMs - a.lastSeenMs;
      return ROSTER.indexOf(a.nick) - ROSTER.indexOf(b.nick);
    });

    return jsonResponse({
      ok: true,
      me,
      serverNowMs: now,
      friends
    });
  }

  handleGetSkin(rawNick) {
    const target = normalizeNick(rawNick);
    if (!target) {
      return jsonResponse({ ok: false, error: "Unknown player" }, 404);
    }

    const row = this.sql.exec(
      "SELECT skin_b64, skin_hash FROM friends_meta WHERE nick = ?",
      target
    ).toArray()[0];

    if (!row || !row.skin_b64) {
      return new Response("No skin uploaded", {
        status: 404,
        headers: { "Cache-Control": "no-store" }
      });
    }

    try {
      const bytes = base64ToBytes(row.skin_b64);
      return new Response(bytes, {
        status: 200,
        headers: {
          "Content-Type": "image/png",
          "Cache-Control": "public, max-age=300",
          "ETag": row.skin_hash ? '"' + row.skin_hash + '"' : "",
          "Access-Control-Allow-Origin": "*"
        }
      });
    } catch {
      return new Response("Corrupt skin", { status: 500 });
    }
  }

  handleGetChat(url) {
    const me = normalizeNick(url.searchParams.get("me"));
    const friend = normalizeNick(url.searchParams.get("friend"));
    if (!me || !friend || me === friend) {
      return jsonResponse({ ok: false, error: "Invalid chat participants" }, 400);
    }

    const afterRaw = parseInt(url.searchParams.get("after") || "0", 10);
    const afterId = Number.isFinite(afterRaw) && afterRaw > 0 ? afterRaw : 0;
    const pairKey = makePairKey(me, friend);

    let rows;
    if (afterId > 0) {
      rows = this.sql.exec(
        "SELECT id, sender, recipient, text, ts_ms FROM messages WHERE pair_key = ? AND id > ? ORDER BY id ASC LIMIT 120",
        pairKey,
        afterId
      ).toArray();
    } else {
      rows = this.sql.exec(
        "SELECT id, sender, recipient, text, ts_ms FROM (SELECT id, sender, recipient, text, ts_ms FROM messages WHERE pair_key = ? ORDER BY id DESC LIMIT 120) ORDER BY id ASC",
        pairKey
      ).toArray();
    }

    const maxPeerRow = this.sql.exec(
      "SELECT MAX(id) AS max_id FROM messages WHERE pair_key = ? AND sender = ? AND recipient = ?",
      pairKey,
      friend,
      me
    ).toArray()[0];

    const maxPeerId = maxPeerRow && maxPeerRow.max_id ? Number(maxPeerRow.max_id) : 0;
    if (maxPeerId > 0) {
      this.sql.exec(
        `INSERT INTO read_state (reader, peer, last_read_id)
         VALUES (?, ?, ?)
         ON CONFLICT(reader, peer) DO UPDATE SET last_read_id = MAX(last_read_id, excluded.last_read_id)`,
        me,
        friend,
        maxPeerId
      );
    }

    const messages = rows.map((r) => ({
      id: Number(r.id),
      from: r.sender,
      to: r.recipient,
      text: r.text,
      tsMs: Number(r.ts_ms)
    }));

    return jsonResponse({
      ok: true,
      me,
      friend,
      messages
    });
  }

  async handlePostChat(request) {
    let body;
    try {
      body = await request.json();
    } catch {
      return jsonResponse({ ok: false, error: "Invalid JSON" }, 400);
    }

    const from = normalizeNick(body && body.from);
    const to = normalizeNick(body && body.to);
    if (!from || !to || from === to) {
      return jsonResponse({ ok: false, error: "Invalid sender or recipient" }, 400);
    }

    const rawText = typeof body.text === "string" ? body.text.replace(/\r\n/g, "\n").trim() : "";
    if (!rawText) {
      return jsonResponse({ ok: false, error: "Empty message" }, 400);
    }
    const text = rawText.slice(0, 500);
    const pairKey = makePairKey(from, to);
    const tsMs = Date.now();

    const row = this.sql.exec(
      "INSERT INTO messages (pair_key, sender, recipient, text, ts_ms) VALUES (?, ?, ?, ?, ?) RETURNING id",
      pairKey,
      from,
      to,
      text,
      tsMs
    ).toArray()[0];

    const newId = Number(row.id);

    // Считаем переписку прочитанной для отправителя на момент отправки
    this.sql.exec(
      `INSERT INTO read_state (reader, peer, last_read_id)
       VALUES (?, ?, ?)
       ON CONFLICT(reader, peer) DO UPDATE SET last_read_id = MAX(last_read_id, excluded.last_read_id)`,
      from,
      to,
      newId
    );

    // Ограничиваем историю на пару, чтобы база не росла бесконечно
    this.sql.exec(
      `DELETE FROM messages WHERE pair_key = ? AND id NOT IN (
         SELECT id FROM messages WHERE pair_key = ? ORDER BY id DESC LIMIT ?
       )`,
      pairKey,
      pairKey,
      MAX_CHAT_HISTORY_PER_PAIR
    );

    return jsonResponse({
      ok: true,
      message: {
        id: newId,
        from,
        to,
        text,
        tsMs
      }
    });
  }
}

export default {
  async fetch(request, env) {
    const url = new URL(request.url);

    if (request.method === "OPTIONS") {
      return new Response(null, {
        status: 204,
        headers: {
          "Access-Control-Allow-Origin": "*",
          "Access-Control-Allow-Methods": "GET, POST, OPTIONS",
          "Access-Control-Allow-Headers": "Content-Type, X-Client-Key"
        }
      });
    }

    if (url.pathname === "/health") {
      const id = env.FRIENDS_HUB.idFromName("civilization-friends-v1");
      return env.FRIENDS_HUB.get(id).fetch(request);
    }

    const expectedKey = (env && env.CLIENT_KEY) ? env.CLIENT_KEY : DEFAULT_CLIENT_KEY;
    const providedKey = request.headers.get("X-Client-Key") || "";
    if (providedKey !== expectedKey) {
      return jsonResponse({ ok: false, error: "Unauthorized" }, 401);
    }

    const id = env.FRIENDS_HUB.idFromName("civilization-friends-v1");
    const stub = env.FRIENDS_HUB.get(id);
    return stub.fetch(request);
  }
};

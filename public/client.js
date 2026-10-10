const STORAGE_KEY = "chat-user-id";
const GROUP_WINDOW = 5 * 60 * 1000;
const AVATAR_COLORS = ["#e01e5a", "#2eb67d", "#ecb22e", "#36c5f0", "#611f69", "#e67e22"];

const joinScreen = document.querySelector("#join-screen");
const joinForm = document.querySelector("#join-form");
const userIdInput = document.querySelector("#user-id");
const joinError = document.querySelector("#join-error");
const newIdButton = document.querySelector("#new-id");
const app = document.querySelector("#app");
const you = document.querySelector("#you");
const memberList = document.querySelector("#member-list");
const memberCount = document.querySelector("#member-count");
const log = document.querySelector("#log");
const logWrap = document.querySelector("#log-wrap");
const composer = document.querySelector("#composer");
const messageInput = document.querySelector("#message");
const sendButton = document.querySelector("#send");
const composeError = document.querySelector("#compose-error");
const leaveButton = document.querySelector("#leave");
const banner = document.querySelector("#banner");
const rejoinButton = document.querySelector("#rejoin");
const live = document.querySelector("#live");

let socket = null;
let connection = 0;
let userId = "";
let members = [];
let entries = [];
let stickToBottom = true;

userIdInput.value = localStorage.getItem(STORAGE_KEY) || suggestId();

newIdButton.addEventListener("click", () => {
  userIdInput.value = suggestId();
  joinError.textContent = "";
  userIdInput.focus();
});

joinForm.addEventListener("submit", (event) => {
  event.preventDefault();
  connect(userIdInput.value.trim());
});

leaveButton.addEventListener("click", () => {
  connection += 1;
  if (socket) socket.close();
  showJoin();
});

rejoinButton.addEventListener("click", () => {
  connect(userId);
});

messageInput.addEventListener("input", () => {
  composeError.textContent = "";
  sendButton.disabled = messageInput.value.trim() === "";
  messageInput.style.height = "auto";
  messageInput.style.height = `${Math.min(messageInput.scrollHeight, 160)}px`;
});

messageInput.addEventListener("keydown", (event) => {
  if (event.key === "Enter" && !event.shiftKey) {
    event.preventDefault();
    composer.requestSubmit();
  }
});

composer.addEventListener("submit", (event) => {
  event.preventDefault();
  const text = messageInput.value.trim();
  if (!text || !socket || socket.readyState !== WebSocket.OPEN) return;
  socket.send(JSON.stringify({ type: "chat", text }));
  messageInput.value = "";
  sendButton.disabled = true;
  messageInput.style.height = "auto";
});

logWrap.addEventListener("scroll", () => {
  stickToBottom = logWrap.scrollHeight - logWrap.scrollTop - logWrap.clientHeight < 80;
});

function suggestId() {
  const alphabet = "abcdefghjkmnpqrstuvwxyz23456789";
  const bytes = new Uint8Array(4);
  crypto.getRandomValues(bytes);
  let suffix = "";
  for (const byte of bytes) suffix += alphabet[byte % alphabet.length];
  return `user-${suffix}`;
}

function connect(nextId) {
  joinError.textContent = "";
  const id = ++connection;
  if (socket) socket.close();

  const protocol = location.protocol === "https:" ? "wss" : "ws";
  socket = new WebSocket(`${protocol}://${location.host}`);
  socket.addEventListener("open", () => {
    if (id !== connection) return;
    socket.send(JSON.stringify({ type: "join", userId: nextId }));
  });
  socket.addEventListener("message", (event) => {
    if (id !== connection) return;
    handle(JSON.parse(event.data));
  });
  socket.addEventListener("close", () => {
    if (id !== connection || app.hidden) return;
    banner.hidden = false;
  });
}

function handle(data) {
  if (data.type === "error") {
    if (app.hidden) joinError.textContent = data.message;
    else composeError.textContent = data.message;
    return;
  }

  if (data.type === "welcome") {
    userId = data.userId;
    localStorage.setItem(STORAGE_KEY, userId);
    members = data.members;
    entries = data.messages.map((message) => ({ kind: "chat", ...message }));
    banner.hidden = true;
    showRoom();
    return;
  }

  if (data.type === "event") {
    members = data.members;
    const verb = data.event === "join" ? "joined" : "left";
    entries.push({
      kind: "system",
      id: `event-${data.userId}-${data.ts}`,
      text: `${data.userId} ${verb} the room`,
      ts: data.ts,
    });
    live.textContent = `${data.userId} ${verb} the room`;
    render();
    return;
  }

  if (data.type === "chat") {
    entries.push({ kind: "chat", ...data.message });
    if (data.message.userId !== userId) {
      live.textContent = `${data.message.userId} says ${data.message.text}`;
    }
    render();
  }
}

function showJoin() {
  app.hidden = true;
  joinScreen.hidden = false;
  banner.hidden = true;
  userIdInput.focus();
}

function showRoom() {
  joinScreen.hidden = true;
  app.hidden = false;
  you.textContent = userId;
  render();
  messageInput.focus();
}

function render() {
  renderMembers();
  renderLog();
}

function renderMembers() {
  const count = members.length;
  memberCount.textContent = count === 1 ? "1 member" : `${count} members`;
  memberList.replaceChildren();
  for (const id of members) {
    const item = document.createElement("li");
    const dot = document.createElement("span");
    dot.className = "presence";
    dot.setAttribute("aria-hidden", "true");
    const name = document.createElement("span");
    name.textContent = id;
    item.append(dot, name);
    if (id === userId) {
      const tag = document.createElement("span");
      tag.className = "you-tag";
      tag.textContent = "you";
      item.append(tag);
    }
    memberList.append(item);
  }
}

function renderLog() {
  const atBottom = stickToBottom;
  log.replaceChildren();

  const chats = entries.filter((entry) => entry.kind === "chat");
  if (chats.length === 0) {
    const empty = document.createElement("li");
    empty.className = "empty";
    const title = document.createElement("h3");
    title.textContent = "This is the very beginning of #general";
    const copy = document.createElement("p");
    copy.textContent = "Messages sent here show up for everyone in the room.";
    empty.append(title, copy);
    log.append(empty);
  }

  let previousChat = null;
  let previousDay = "";
  for (const entry of entries) {
    const day = dayKey(entry.ts);
    if (day !== previousDay) {
      previousDay = day;
      const divider = document.createElement("li");
      divider.className = "day";
      divider.textContent = formatDay(entry.ts);
      log.append(divider);
    }

    if (entry.kind === "system") {
      const item = document.createElement("li");
      item.className = "system";
      item.textContent = entry.text;
      log.append(item);
      previousChat = null;
      continue;
    }

    const grouped =
      previousChat &&
      previousChat.userId === entry.userId &&
      entry.ts - previousChat.ts < GROUP_WINDOW &&
      dayKey(previousChat.ts) === day;

    const item = document.createElement("li");
    item.className = grouped ? "message compact" : "message full";

    if (!grouped) {
      const avatar = document.createElement("span");
      avatar.className = "avatar";
      avatar.style.background = avatarColor(entry.userId);
      avatar.textContent = entry.userId.slice(0, 2).toUpperCase();
      avatar.setAttribute("aria-hidden", "true");

      const content = document.createElement("div");
      const meta = document.createElement("div");
      meta.className = "meta";
      const name = document.createElement("span");
      name.className = "name";
      name.textContent = entry.userId;
      const time = document.createElement("time");
      time.className = "time";
      time.dateTime = new Date(entry.ts).toISOString();
      time.textContent = formatTime(entry.ts);
      meta.append(name, time);
      const body = document.createElement("p");
      body.className = "body";
      body.textContent = entry.text;
      content.append(meta, body);
      item.append(avatar, content);
    } else {
      const body = document.createElement("p");
      body.className = "body";
      body.textContent = entry.text;
      item.append(body);
    }

    log.append(item);
    previousChat = entry;
  }

  if (atBottom) logWrap.scrollTop = logWrap.scrollHeight;
}

function avatarColor(id) {
  let hash = 0;
  for (let index = 0; index < id.length; index += 1) {
    hash = (hash * 33 + id.charCodeAt(index)) >>> 0;
  }
  return AVATAR_COLORS[hash % AVATAR_COLORS.length];
}

function dayKey(ts) {
  const date = new Date(ts);
  return `${date.getFullYear()}-${date.getMonth()}-${date.getDate()}`;
}

function formatDay(ts) {
  const today = dayKey(Date.now());
  const yesterdayDate = new Date();
  yesterdayDate.setDate(yesterdayDate.getDate() - 1);
  const key = dayKey(ts);
  if (key === today) return "Today";
  if (key === dayKey(yesterdayDate.getTime())) return "Yesterday";
  return new Intl.DateTimeFormat(undefined, {
    month: "long",
    day: "numeric",
    year: "numeric",
  }).format(ts);
}

function formatTime(ts) {
  return new Intl.DateTimeFormat(undefined, {
    hour: "numeric",
    minute: "2-digit",
  }).format(ts);
}

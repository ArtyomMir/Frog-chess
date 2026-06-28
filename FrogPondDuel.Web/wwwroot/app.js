const state = {
  player: JSON.parse(localStorage.getItem("frogpond.player") || "null"),
  matchId: localStorage.getItem("frogpond.matchId") || "",
  match: null,
  path: [],
  hub: null
};

const ui = {
  connection: document.querySelector("#connection"),
  joinForm: document.querySelector("#joinForm"),
  nameInput: document.querySelector("#nameInput"),
  matchInput: document.querySelector("#matchInput"),
  matchIdText: document.querySelector("#matchIdText"),
  copyButton: document.querySelector("#copyButton"),
  sendJumpButton: document.querySelector("#sendJumpButton"),
  resetPathButton: document.querySelector("#resetPathButton"),
  passButton: document.querySelector("#passButton"),
  scorebar: document.querySelector("#scorebar"),
  board: document.querySelector("#board"),
  events: document.querySelector("#events")
};

ui.nameInput.value = state.player?.name || "";
ui.matchInput.value = state.matchId;

ui.joinForm.addEventListener("submit", async (event) => {
  event.preventDefault();

  const playerName = ui.nameInput.value.trim();
  const matchId = ui.matchInput.value.trim();
  const route = matchId ? `/api/games/${matchId}/join` : "/api/games";
  const result = await api(route, {
    method: "POST",
    body: { playerName }
  });

  setSession(result.player, result.match);
});

ui.copyButton.addEventListener("click", async () => {
  if (state.matchId) {
    await navigator.clipboard.writeText(state.matchId);
  }
});

ui.sendJumpButton.addEventListener("click", async () => {
  if (state.path.length < 2) {
    return;
  }

  const snapshot = await api(`/api/games/${state.matchId}/jumps`, {
    method: "POST",
    body: {
      playerToken: state.player.playerToken,
      path: state.path
    }
  });

  state.path = [];
  setMatch(snapshot);
});

ui.resetPathButton.addEventListener("click", () => {
  state.path = [];
  render();
});

ui.passButton.addEventListener("click", async () => {
  if (!state.matchId || !state.player) {
    return;
  }

  const snapshot = await api(`/api/games/${state.matchId}/pass`, {
    method: "POST",
    body: { playerToken: state.player.playerToken }
  });

  state.path = [];
  setMatch(snapshot);
});

ui.board.addEventListener("click", async (event) => {
  const cell = event.target.closest(".cell");

  if (!cell || !state.match || !state.player) {
    return;
  }

  const point = { row: Number(cell.dataset.row), col: Number(cell.dataset.col) };
  const current = currentPlayer();

  if (!isMyTurn() || !current) {
    return;
  }

  const piece = pieceAt(point);

  if (!current.removedOpeningFrog) {
    if (!piece) {
      return;
    }

    const snapshot = await api(`/api/games/${state.matchId}/remove`, {
      method: "POST",
      body: {
        playerToken: state.player.playerToken,
        row: point.row,
        col: point.col
      }
    });

    setMatch(snapshot);
    return;
  }

  if (state.path.length === 0) {
    if (piece?.ownerId === state.player.playerId) {
      state.path = [point];
    }
  } else {
    state.path.push(point);
  }

  render();
});

setInterval(refreshMatch, 1500);
render();
if (state.matchId && state.player) {
  refreshMatch();
}

async function setSession(player, match) {
  state.player = player;
  state.matchId = match.matchId;
  localStorage.setItem("frogpond.player", JSON.stringify(player));
  localStorage.setItem("frogpond.matchId", match.matchId);
  ui.matchInput.value = match.matchId;
  setMatch(match);
  await connectHub();
}

function setMatch(match) {
  state.match = match;
  state.matchId = match.matchId;
  localStorage.setItem("frogpond.matchId", match.matchId);
  render();
}

async function refreshMatch() {
  if (!state.matchId || !state.player) {
    return;
  }

  try {
    const snapshot = await api(`/api/games/${state.matchId}?playerToken=${state.player.playerToken}`);
    setMatch(snapshot);
    await connectHub();
  } catch (error) {
    ui.connection.textContent = "error";
  }
}

async function connectHub() {
  if (!window.signalR || state.hub || !state.matchId || !state.player) {
    return;
  }

  const hub = new signalR.HubConnectionBuilder()
    .withUrl("/gameHub")
    .withAutomaticReconnect()
    .build();

  hub.on("stateChanged", (snapshot) => {
    state.match = snapshot;
    render();
  });

  hub.onreconnected(() => hub.invoke("WatchGame", state.matchId, state.player.playerToken));

  try {
    await hub.start();
    await hub.invoke("WatchGame", state.matchId, state.player.playerToken);
    state.hub = hub;
    ui.connection.textContent = "signalr";
  } catch {
    ui.connection.textContent = "polling";
  }
}

async function api(route, options = {}) {
  const response = await fetch(route, {
    method: options.method || "GET",
    headers: { "Content-Type": "application/json" },
    body: options.body ? JSON.stringify(options.body) : undefined
  });

  const payload = await response.json();
  if (!response.ok) {
    throw new Error(payload.error || "Request failed.");
  }

  if (!state.hub) {
    ui.connection.textContent = "polling";
  }

  return payload;
}

function render() {
  ui.matchIdText.textContent = state.matchId || "-";
  ui.sendJumpButton.disabled = state.path.length < 2;
  ui.resetPathButton.disabled = state.path.length === 0;
  renderStatus();
  renderBoard();
  renderEvents();
}

function renderStatus() {
  if (!state.match) {
    ui.scorebar.textContent = "Создайте матч или войдите по ID";
    return;
  }

  const p1 = state.match.playerOne;
  const p2 = state.match.playerTwo;
  const turn = [p1, p2].find((player) => player?.playerId === state.match.turnPlayerId);
  const winner = [p1, p2].find((player) => player?.playerId === state.match.winnerPlayerId);
  const names = p2 ? `${p1.name} против ${p2.name}` : `${p1.name} ожидает соперника`;

  if (state.match.status === "Finished") {
    ui.scorebar.textContent = winner ? `Игра окончена. Победитель: ${winner.name}` : "Игра окончена без победителя";
    return;
  }

  ui.scorebar.textContent = turn ? `${names}. Ходит: ${turn.name}` : names;
}

function renderBoard() {
  const board = [];
  for (let row = 0; row < 8; row++) {
    for (let col = 0; col < 8; col++) {
      const point = { row, col };
      const piece = pieceAt(point);
      const swamp = row === 0 || row === 7 || col === 0 || col === 7;
      const pathIndex = state.path.findIndex((step) => samePoint(step, point));
      const classes = ["cell", swamp ? "swamp" : "", pathIndex === 0 ? "selected" : "", pathIndex > 0 ? "path" : ""]
        .filter(Boolean)
        .join(" ");

      board.push(`
        <button class="${classes}" data-row="${row}" data-col="${col}" type="button">
          <span class="coord">${row}:${col}</span>
          ${piece ? `<span class="frog ${piece.side.toLowerCase()}"></span>` : ""}
        </button>
      `);
    }
  }

  ui.board.innerHTML = board.join("");
}

function renderEvents() {
  const events = state.match?.events || [];
  ui.events.innerHTML = events
    .slice()
    .reverse()
    .map((event) => `<li><strong>${event.kind}</strong>: ${escapeHtml(event.text)}</li>`)
    .join("");
}

function currentPlayer() {
  if (!state.match || !state.player) {
    return null;
  }

  return [state.match.playerOne, state.match.playerTwo]
    .find((player) => player?.playerId === state.player.playerId) || null;
}

function isMyTurn() {
  return state.match?.status === "Playing" && state.match.turnPlayerId === state.player?.playerId;
}

function pieceAt(point) {
  return state.match?.board?.find((piece) => samePoint(piece.position, point)) || null;
}

function samePoint(first, second) {
  return first.row === second.row && first.col === second.col;
}

function escapeHtml(value) {
  return String(value).replace(/[&<>"']/g, (char) => ({
    "&": "&amp;",
    "<": "&lt;",
    ">": "&gt;",
    "\"": "&quot;",
    "'": "&#039;"
  })[char]);
}

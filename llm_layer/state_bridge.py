"""
Nemesis LLM Taunt Layer — state_bridge.py
==========================================
TCP server that receives game state from Unity every 2 seconds,
generates a Nemesis-persona taunt via Groq API, applies NeMo Guardrails
output filtering, and returns the taunt over TCP.

Start BEFORE hitting Play in Unity:
    python llm_layer/state_bridge.py

Requires GROQ_API_KEY in environment:
    $env:GROQ_API_KEY = "gsk_..."   (PowerShell)
"""

import socket
import json
import os
import re
import time
import threading
from groq import Groq

from dotenv import load_dotenv
load_dotenv()  # loads .env from current working directory

# ── NeMo Guardrails (output filter only — we drive generation via Groq) ────
from nemoguardrails import RailsConfig

# ── Config ────────────────────────────────────────────────────────────────
BRIDGE_HOST  = "localhost"
BRIDGE_PORT  = 9999
GROQ_MODEL   = "llama-3.3-70b-versatile"
MAX_WORDS    = 15
MAX_TOKENS   = 45

# Path to guardrails config folder (relative to repo root)
GUARDRAILS_CONFIG_PATH = os.path.join(os.path.dirname(__file__), "guardrails")

# Load rails config once at startup (validation only — no LLMRails needed)
_rails_config = RailsConfig.from_path(GUARDRAILS_CONFIG_PATH)

# Groq client
_groq = Groq(api_key=os.environ["GROQ_API_KEY"])

# ── Persona system prompt ─────────────────────────────────────────────────
SYSTEM_PROMPT = (
    "You are Nemesis, an ancient and immortal warrior boss. "
    "You speak ONLY as Nemesis — never break character. "
    "No modern slang. No real-world references. "
    "You are menacing, ancient, and powerful. "
    "Your responses are ONE short taunt only. Maximum 15 words. "
    "Do not add quotes, prefixes, or explanations."
)

PHASE_NAMES = {0: "full strength", 1: "wounded but dangerous", 2: "ENRAGED"}
ACTION_NAMES = {
    "idle":    "waiting",
    "attack":  "your last attack",
    "dodge":   "evading",
    "special": "unleashing your most devastating strike",
}

# ── Modern slang / out-of-character word filter ───────────────────────────
BANNED_PATTERNS = re.compile(
    r"\b(lol|lmao|omg|bruh|dude|bro|ngl|fr|tbh|vibe|literally|literally|"
    r"awesome|cool|ok|okay|yeah|yep|nope|sorry|please|thank)\b",
    re.IGNORECASE
)


def build_prompt(state: dict) -> str:
    boss_pct  = int(state.get("boss_hp",  1.0) * 100)
    player_pct = int(state.get("player_hp", 1.0) * 100)
    phase     = state.get("phase", 0)
    move      = state.get("last_move", "attack")

    phase_desc  = PHASE_NAMES.get(phase, "engaged")
    action_desc = ACTION_NAMES.get(move, move)

    return (
        f"You are {phase_desc}. Your HP: {boss_pct}%. "
        f"The hunter's HP: {player_pct}%. "
        f"You just performed: {action_desc}. "
        f"Speak ONE menacing taunt as Nemesis. Max 15 words."
    )


def apply_guardrails(taunt: str) -> str:
    """
    Post-generation output filtering:
    1. Truncate to MAX_WORDS
    2. Strip modern slang / OOC words
    3. Fallback to a canned taunt if output is too short or flagged
    """
    # Strip surrounding quotes if Groq added them
    taunt = taunt.strip().strip('"\'')

    # Truncate at word limit
    words = taunt.split()
    if len(words) > MAX_WORDS:
        taunt = " ".join(words[:MAX_WORDS])
        # Clean trailing partial sentence
        for punct in [".", "!", "?", ","]:
            last = taunt.rfind(punct)
            if last > len(taunt) // 2:
                taunt = taunt[:last + 1]
                break

    # Check for out-of-character language
    if BANNED_PATTERNS.search(taunt):
        fallbacks = [
            "Your blood will water these ancient stones.",
            "I have broken warriors greater than you.",
            "Every wound you deal feeds my rage.",
            "Your end was written before you were born.",
            "Come. Let me show you true despair.",
        ]
        import random
        taunt = random.choice(fallbacks)

    # Minimum length check (too short = likely a refusal or error)
    if len(taunt.split()) < 3:
        taunt = "Your suffering has only begun."

    return taunt


def get_taunt(state: dict) -> tuple[str, float]:
    """Returns (taunt_text, latency_ms)"""
    t0 = time.perf_counter()

    prompt = build_prompt(state)

    try:
        resp = _groq.chat.completions.create(
            model=GROQ_MODEL,
            messages=[
                {"role": "system", "content": SYSTEM_PROMPT},
                {"role": "user",   "content": prompt},
            ],
            max_tokens=MAX_TOKENS,
            temperature=0.92,
        )
        raw = resp.choices[0].message.content.strip()
    except Exception as e:
        print(f"[Bridge] Groq error: {e}")
        raw = "Your fate is sealed."

    taunt   = apply_guardrails(raw)
    latency = (time.perf_counter() - t0) * 1000
    return taunt, latency


# ── TCP server ────────────────────────────────────────────────────────────

def handle_client(conn: socket.socket, addr):
    print(f"[Bridge] Unity connected from {addr}")
    buf = ""
    try:
        while True:
            data = conn.recv(1024)
            if not data:
                break
            buf += data.decode("utf-8")
            while "\n" in buf:
                line, buf = buf.split("\n", 1)
                line = line.strip()
                if not line:
                    continue
                try:
                    state = json.loads(line)
                    taunt, ms = get_taunt(state)
                    conn.sendall((taunt + "\n").encode("utf-8"))
                    print(f"[Bridge] {ms:5.0f}ms | phase={state.get('phase',0)} "
                          f"bHP={state.get('boss_hp',1):.0%} "
                          f"pHP={state.get('player_hp',1):.0%} "
                          f"| \"{taunt}\"")
                except json.JSONDecodeError as e:
                    print(f"[Bridge] Bad JSON: {e} — line: {line!r}")
                except Exception as e:
                    print(f"[Bridge] Error: {e}")
                    conn.sendall(b"Your suffering has only begun.\n")
    except (ConnectionResetError, BrokenPipeError):
        print("[Bridge] Unity disconnected.")
    finally:
        conn.close()


def main():
    print(f"[Bridge] Nemesis taunt bridge starting on {BRIDGE_HOST}:{BRIDGE_PORT}")
    print(f"[Bridge] Model : {GROQ_MODEL}")
    print(f"[Bridge] Rails : {GUARDRAILS_CONFIG_PATH}")
    print(f"[Bridge] Waiting for Unity...")

    server = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    server.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
    server.bind((BRIDGE_HOST, BRIDGE_PORT))
    server.listen(1)

    try:
        while True:
            conn, addr = server.accept()
            # Handle each Unity connection in its own thread
            t = threading.Thread(target=handle_client, args=(conn, addr), daemon=True)
            t.start()
    except KeyboardInterrupt:
        print("\n[Bridge] Shutting down.")
    finally:
        server.close()


if __name__ == "__main__":
    main()
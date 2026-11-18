// /why 頁面：選朋友 → 按住說話 → 角色思考 → 一邊顯示一邊唸出答案（角色嘴巴跟著動）。
// 全部是單一 HTML（SVG 角色 + CSS 動畫 + 瀏覽器內建語音辨識 / 語音朗讀），不依賴外部套件。
public static class WhyKidsPage
{
    public const string Html = """
<!doctype html>
<html lang="zh-Hant-TW">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1, viewport-fit=cover, user-scalable=no">
<meta name="theme-color" content="#fff6e0">
<title>為什麼小學堂</title>
<style>
:root{
  --bg1:#fff6e0; --bg2:#ffe9c2; --ink:#3b2f1e; --muted:#8a7a5c;
  --accent:#1c69d4; --accent-soft:#dce9fb; --bubble:#ffffff; --kid:#fff2b3;
}
body.dog{ --accent:#c97b34; --accent-soft:#f8e4cf; }
*{box-sizing:border-box;-webkit-tap-highlight-color:transparent}
html,body{height:100%}
body{margin:0;background:linear-gradient(180deg,var(--bg1),var(--bg2));color:var(--ink);
  font:18px/1.6 -apple-system,BlinkMacSystemFont,"PingFang TC","Noto Sans TC","Microsoft JhengHei",sans-serif;
  -webkit-user-select:none;user-select:none;-webkit-touch-callout:none;overflow:hidden}
.screen{position:fixed;inset:0;display:flex;flex-direction:column;
  padding:calc(env(safe-area-inset-top) + 12px) 16px calc(env(safe-area-inset-bottom) + 16px)}
[hidden]{display:none!important}

/* ---------- 選朋友 ---------- */
#home{align-items:center;justify-content:center;gap:20px}
#home h1{margin:0;font-size:1.7rem;letter-spacing:.05em}
#home p{margin:0;color:var(--muted)}
.picks{display:flex;flex-direction:column;gap:16px;width:100%;max-width:420px}
.pick{display:flex;align-items:center;gap:14px;border:none;border-radius:28px;padding:12px 18px;
  font:inherit;font-size:1.4rem;font-weight:700;color:var(--ink);cursor:pointer;
  box-shadow:0 6px 0 rgba(0,0,0,.12);transition:transform .1s}
.pick:active{transform:translateY(4px);box-shadow:0 2px 0 rgba(0,0,0,.12)}
.pick.car{background:#dce9fb}.pick.dog{background:#f8e4cf}
.pick svg{width:110px;height:95px;flex:none}
.pick small{display:block;font-size:.95rem;font-weight:400;color:var(--muted)}

/* ---------- 聊天 ---------- */
#chat{gap:10px}
.top{display:flex;align-items:center;justify-content:space-between}
.btn-lite{border:none;background:rgba(255,255,255,.7);border-radius:999px;padding:6px 14px;
  font:inherit;font-size:.95rem;color:var(--ink);cursor:pointer}
#who{font-weight:700;font-size:1.15rem;color:var(--accent)}
.stage{flex:1 1 auto;min-height:0;display:flex;flex-direction:column;align-items:center;justify-content:flex-end;gap:6px}
.bubble{position:relative;width:100%;max-width:520px;background:var(--bubble);border-radius:24px;
  padding:14px 18px;font-size:1.25rem;line-height:1.65;box-shadow:0 4px 14px rgba(0,0,0,.08);
  max-height:34vh;overflow-y:auto;scrollbar-width:none;-webkit-user-select:text;user-select:text}
.bubble::-webkit-scrollbar{display:none}
.bubble:after{content:"";position:absolute;left:50%;bottom:-12px;margin-left:-12px;
  border:12px solid transparent;border-top-color:var(--bubble);border-bottom:0}
.bubble.kid{background:var(--kid);font-size:1.1rem}
.bubble.kid:after{border-top-color:var(--kid)}
.bubble:empty{display:none}
#heard:empty{display:none}
#heard{align-self:flex-end;background:var(--kid);border-radius:18px 18px 4px 18px;padding:6px 14px;
  font-size:1rem;max-width:85%}
.charbox{position:relative;width:min(78vw,340px);aspect-ratio:300/260;flex:none}
.charbox svg{width:100%;height:100%;overflow:visible}
.dots{position:absolute;top:-6px;right:4%;display:none;gap:6px;background:#fff;border-radius:20px;padding:8px 12px;
  box-shadow:0 2px 8px rgba(0,0,0,.1)}
.dots i{width:10px;height:10px;border-radius:50%;background:var(--accent);animation:dot 1s infinite}
.dots i:nth-child(2){animation-delay:.15s}.dots i:nth-child(3){animation-delay:.3s}
.thinking .dots{display:flex}
@keyframes dot{0%,100%{transform:translateY(0);opacity:.4}50%{transform:translateY(-6px);opacity:1}}

.chips{display:flex;gap:8px;overflow-x:auto;padding:2px 2px 4px;scrollbar-width:none;flex:none}
.chips::-webkit-scrollbar{display:none}
.chip{flex:none;border:2px solid var(--accent-soft);background:#fff;border-radius:999px;padding:6px 14px;
  font:inherit;font-size:1rem;color:var(--ink);cursor:pointer}

.controls{display:flex;flex-direction:column;align-items:center;gap:8px;flex:none}
#mic{width:128px;height:128px;border-radius:50%;border:none;background:var(--accent);color:#fff;
  font:inherit;font-size:1.05rem;font-weight:700;display:flex;flex-direction:column;align-items:center;justify-content:center;
  box-shadow:0 8px 0 rgba(0,0,0,.18);cursor:pointer;touch-action:none;transition:transform .1s}
#mic svg{width:46px;height:46px;margin-bottom:2px}
#mic.on{transform:scale(1.08);box-shadow:0 0 0 12px var(--accent-soft),0 0 0 24px rgba(255,255,255,.5)}
#typebar{display:flex;gap:8px;width:100%;max-width:520px}
#typebar input{flex:1;min-width:0;border:2px solid var(--accent-soft);border-radius:999px;padding:10px 16px;font:inherit;font-size:16px;
  -webkit-user-select:text;user-select:text;background:#fff;color:var(--ink)}
#typebar button{border:none;border-radius:999px;background:var(--accent);color:#fff;padding:0 18px;font:inherit;font-size:1rem}
.hint{font-size:.85rem;color:var(--muted);text-align:center;min-height:1.3em}

/* ---------- 角色動畫 ---------- */
.char{animation:bob 2.6s ease-in-out infinite;transform-origin:50% 100%}
@keyframes bob{0%,100%{transform:translateY(0)}50%{transform:translateY(-6px)}}
.talking .char{animation:bounce .5s ease-in-out infinite}
@keyframes bounce{0%,100%{transform:translateY(0) scale(1,1)}50%{transform:translateY(-5px) scale(1.01,.99)}}
.listening .char{animation:lean 1.2s ease-in-out infinite}
@keyframes lean{0%,100%{transform:rotate(-2deg)}50%{transform:rotate(2deg)}}
.happy .char{animation:jump .45s ease-out 2}
@keyframes jump{0%,100%{transform:translateY(0)}40%{transform:translateY(-22px)}}
.eye{transform-box:fill-box;transform-origin:center;animation:blink 4.5s infinite}
@keyframes blink{0%,94%,100%{transform:scaleY(1)}96%{transform:scaleY(.1)}}
.pupil{transition:transform .3s}
.thinking .pupil{transform:translate(4px,-7px)}
.listening .pupil{transform:scale(1.15)}
.mouth{transform-box:fill-box;transform-origin:50% 0;transform:scaleY(.35);transition:transform .15s}
.listening .mouth{transform:scaleY(.6)}
.happy .mouth{transform:scaleY(.9)}
.talking .mouth{animation:talk .2s ease-in-out infinite alternate}
@keyframes talk{from{transform:scaleY(.3)}to{transform:scaleY(1)}}
.lights{opacity:0;transition:opacity .2s}
.listening .lights,.talking .lights{opacity:1}
.talking .lights{animation:flash .4s infinite alternate}
@keyframes flash{from{opacity:.5}to{opacity:1}}
.ear{transform-box:fill-box;transition:transform .3s}
.ear.l{transform-origin:85% 10%}.ear.r{transform-origin:15% 10%}
.listening .ear.l{transform:rotate(18deg)}.listening .ear.r{transform:rotate(-18deg)}
.talking .ear.l{animation:earL .5s infinite alternate}.talking .ear.r{animation:earR .5s infinite alternate}
@keyframes earL{to{transform:rotate(10deg)}}@keyframes earR{to{transform:rotate(-10deg)}}
.tail{transform-box:fill-box;transform-origin:0% 100%;animation:wag 1.2s ease-in-out infinite}
.talking .tail,.happy .tail,.listening .tail{animation-duration:.3s}
@keyframes wag{0%,100%{transform:rotate(-12deg)}50%{transform:rotate(16deg)}}
.wheel{transform-box:fill-box;transform-origin:center}
.ring{fill:none;stroke:var(--accent);stroke-width:4;opacity:0}
.listening .ring{animation:ring 1.2s ease-out infinite}
@keyframes ring{from{opacity:.6;transform:scale(.85)}to{opacity:0;transform:scale(1.15)}}
.ring{transform-box:fill-box;transform-origin:center}
@media (prefers-reduced-motion:reduce){*{animation-duration:0s!important;animation-iteration-count:1!important}}
</style>
</head>
<body>

<!-- 選朋友 -->
<section id="home" class="screen">
  <h1>為什麼小學堂</h1>
  <p>今天要跟誰聊天呢？</p>
  <div class="picks">
    <button class="pick car" data-kid="car"><span class="mini" data-svg="car"></span><span>藍寶<small>哥哥的跑車朋友</small></span></button>
    <button class="pick dog" data-kid="dog"><span class="mini" data-svg="dog"></span><span>豆豆<small>妹妹的狗狗朋友</small></span></button>
  </div>
</section>

<!-- 聊天 -->
<section id="chat" class="screen" hidden>
  <div class="top">
    <button class="btn-lite" id="back">← 換朋友</button>
    <span id="who"></span>
    <button class="btn-lite" id="kbd" aria-label="打字">⌨️</button>
  </div>
  <div class="stage idle" id="stage">
    <div id="heard"></div>
    <div class="bubble" id="bubble"></div>
    <div class="charbox"><div class="dots"><i></i><i></i><i></i></div><div id="charsvg"></div></div>
  </div>
  <div class="chips" id="chips"></div>
  <div class="controls">
    <button id="mic" aria-label="按住說話">
      <svg viewBox="0 0 24 24" fill="currentColor"><path d="M12 15a3.5 3.5 0 0 0 3.5-3.5v-6a3.5 3.5 0 0 0-7 0v6A3.5 3.5 0 0 0 12 15Zm6-3.5a1 1 0 1 0-2 0 4 4 0 0 1-8 0 1 1 0 1 0-2 0 6 6 0 0 0 5 5.9V20H9a1 1 0 1 0 0 2h6a1 1 0 1 0 0-2h-2v-2.6a6 6 0 0 0 5-5.9Z"/></svg>
      <span id="micLabel">按住說話</span>
    </button>
    <form id="typebar" hidden><input id="typed" placeholder="打字問問題…" enterkeyhint="send" autocomplete="off"><button>問</button></form>
    <div class="hint" id="hint"></div>
  </div>
</section>

<script>
// ---------------- 角色 SVG ----------------
const CAR_SVG = `
<svg viewBox="0 0 300 260" class="charsvg" aria-hidden="true">
 <ellipse cx="150" cy="246" rx="120" ry="10" fill="rgba(0,0,0,.12)"/>
 <ellipse class="ring" cx="150" cy="145" rx="140" ry="105"/>
 <g class="char">
  <rect class="wheel" x="38" y="196" width="46" height="48" rx="14" fill="#2b2b2b"/>
  <rect class="wheel" x="216" y="196" width="46" height="48" rx="14" fill="#2b2b2b"/>
  <path d="M78 102 L104 42 Q108 34 118 34 L182 34 Q192 34 196 42 L222 102 Z" fill="#1c69d4"/>
  <path d="M96 98 L116 50 Q118 46 123 46 L177 46 Q182 46 184 50 L204 98 Z" fill="#bfe0ff"/>
  <path d="M112 60 L124 52 L132 52 L118 66 Z" fill="#fff" opacity=".8"/>
  <rect x="24" y="92" width="252" height="124" rx="46" fill="#1c69d4"/>
  <rect x="24" y="92" width="252" height="40" rx="20" fill="#3d86ea" opacity=".55"/>
  <ellipse cx="62" cy="170" rx="16" ry="10" fill="#ff9db5" opacity=".75"/>
  <ellipse cx="238" cy="170" rx="16" ry="10" fill="#ff9db5" opacity=".75"/>
  <g class="lights"><ellipse cx="82" cy="138" rx="40" ry="34" fill="#fff6a8" opacity=".55"/><ellipse cx="218" cy="138" rx="40" ry="34" fill="#fff6a8" opacity=".55"/></g>
  <g class="eye"><circle cx="82" cy="138" r="27" fill="#fff" stroke="#c9d6e8" stroke-width="4"/>
   <g class="pupil"><circle cx="86" cy="140" r="12" fill="#1d2433"/><circle cx="91" cy="134" r="4" fill="#fff"/></g></g>
  <g class="eye"><circle cx="218" cy="138" r="27" fill="#fff" stroke="#c9d6e8" stroke-width="4"/>
   <g class="pupil"><circle cx="214" cy="140" r="12" fill="#1d2433"/><circle cx="219" cy="134" r="4" fill="#fff"/></g></g>
  <g>
   <rect x="120" y="126" width="28" height="22" rx="10" fill="#cfd8e3" stroke="#9aa9bb" stroke-width="3"/>
   <rect x="152" y="126" width="28" height="22" rx="10" fill="#cfd8e3" stroke="#9aa9bb" stroke-width="3"/>
  </g>
  <g class="mouth">
   <rect x="120" y="150" width="28" height="44" rx="11" fill="#22252b" stroke="#c0c8d2" stroke-width="4"/>
   <rect x="152" y="150" width="28" height="44" rx="11" fill="#22252b" stroke="#c0c8d2" stroke-width="4"/>
  </g>
  <rect x="40" y="200" width="220" height="14" rx="7" fill="#1558b3"/>
  <path d="M150 100 L154 108 L162 109 L156 115 L158 123 L150 119 L142 123 L144 115 L138 109 L146 108 Z" fill="#ffd54a" stroke="#e0a800" stroke-width="2" stroke-linejoin="round"/>
 </g>
</svg>`;

const DOG_SVG = `
<svg viewBox="0 0 300 260" class="charsvg" aria-hidden="true">
 <ellipse cx="150" cy="248" rx="105" ry="10" fill="rgba(0,0,0,.12)"/>
 <ellipse class="ring" cx="150" cy="140" rx="135" ry="110"/>
 <g class="char">
  <path class="tail" d="M236 206 Q274 186 268 150 Q266 140 258 146 Q262 178 228 192 Z" fill="#c98a4b"/>
  <ellipse cx="150" cy="222" rx="78" ry="30" fill="#d9a066"/>
  <ellipse cx="112" cy="238" rx="20" ry="12" fill="#f7e3c6"/>
  <ellipse cx="188" cy="238" rx="20" ry="12" fill="#f7e3c6"/>
  <path class="ear l" d="M78 70 Q38 70 30 130 Q26 170 56 176 Q80 178 88 130 Z" fill="#8b5a2b"/>
  <path class="ear r" d="M222 70 Q262 70 270 130 Q274 170 244 176 Q220 178 212 130 Z" fill="#8b5a2b"/>
  <ellipse cx="150" cy="120" rx="92" ry="86" fill="#d9a066"/>
  <ellipse cx="190" cy="104" rx="30" ry="26" fill="#b97a3f"/>
  <ellipse cx="150" cy="160" rx="54" ry="40" fill="#f7e3c6"/>
  <ellipse cx="96" cy="150" rx="15" ry="9" fill="#ff9db5" opacity=".7"/>
  <ellipse cx="204" cy="150" rx="15" ry="9" fill="#ff9db5" opacity=".7"/>
  <g class="eye"><g class="pupil"><circle cx="112" cy="108" r="15" fill="#2a1d14"/><circle cx="117" cy="102" r="5" fill="#fff"/></g></g>
  <g class="eye"><g class="pupil"><circle cx="188" cy="108" r="15" fill="#2a1d14"/><circle cx="193" cy="102" r="5" fill="#fff"/></g></g>
  <ellipse cx="150" cy="140" rx="17" ry="12" fill="#2a1d14"/>
  <ellipse cx="145" cy="136" rx="5" ry="3" fill="#fff" opacity=".6"/>
  <path d="M150 152 L150 162 M150 162 Q138 172 128 164 M150 162 Q162 172 172 164" stroke="#2a1d14" stroke-width="4" fill="none" stroke-linecap="round"/>
  <g class="mouth">
   <path d="M132 166 Q150 168 168 166 Q166 196 150 198 Q134 196 132 166 Z" fill="#7a2b2b"/>
   <ellipse cx="150" cy="188" rx="11" ry="9" fill="#ff7f9a"/>
  </g>
 </g>
</svg>`;

// ---------------- 角色設定 ----------------
const KIDS = {
  car: { name:"藍寶", who:"藍寶 🚗", svg:CAR_SVG, pitch:1.25, rate:1.0,
    greet:"叭叭！哥哥你好，我是藍寶！有什麼想知道的，按住下面的藍色按鈕問我吧！",
    chips:["為什麼車子會跑？","BMW 是哪裡做的？","為什麼天空是藍色的？","為什麼要繫安全帶？","電動車怎麼加油？","為什麼會下雨？"] },
  dog: { name:"豆豆", who:"豆豆 🐶", svg:DOG_SVG, pitch:1.6, rate:0.95,
    greet:"汪汪！妹妹你好，我是豆豆！你想問我什麼呢？按住下面的按鈕說說看！",
    chips:["狗狗為什麼搖尾巴？","為什麼狗狗鼻子濕濕的？","月亮為什麼會跟著我？","為什麼要刷牙？","彩虹是怎麼來的？","為什麼晚上要睡覺？"] },
};

const $ = id => document.getElementById(id);
const stage = $("stage"), bubble = $("bubble"), heard = $("heard"), hint = $("hint");
let kid = null, history = [], busy = false, aborter = null;

document.querySelectorAll(".mini").forEach(el => el.innerHTML = KIDS[el.dataset.svg].svg);

function setState(s){ stage.className = "stage " + s; }

// ---------------- 語音朗讀（瀏覽器內建） ----------------
const synth = window.speechSynthesis;
let voice = null, speakingCount = 0, streamDone = true;
function pickVoice(){
  const vs = synth ? synth.getVoices() : [];
  voice = vs.find(v => v.lang === "zh-TW") || vs.find(v => /zh[-_]TW|Hant/i.test(v.lang))
       || vs.find(v => /^zh/i.test(v.lang)) || null;
}
if (synth){ pickVoice(); synth.onvoiceschanged = pickVoice; }

function cleanForSpeech(t){
  return t.replace(/[\u{1F300}-\u{1FAFF}\u{2600}-\u{27BF}\u{FE0F}]/gu, "").replace(/[*#_`>~\-]+/g, " ").trim();
}
function speak(text){
  text = cleanForSpeech(text);
  if (!synth || !text) return;
  const u = new SpeechSynthesisUtterance(text);
  u.lang = "zh-TW"; if (voice) u.voice = voice;
  u.pitch = KIDS[kid].pitch; u.rate = KIDS[kid].rate;
  speakingCount++;
  const done = () => { speakingCount = Math.max(0, speakingCount - 1); maybeIdle(); };
  u.onstart = () => setState("talking");
  u.onend = done; u.onerror = done;
  synth.speak(u);
}
function maybeIdle(){
  if (speakingCount === 0 && streamDone && !stage.classList.contains("listening")) setState("idle");
}
function stopSpeaking(){ if (synth) synth.cancel(); speakingCount = 0; }

// 串流文字切成句子，一句一句唸
let sentenceBuf = "";
function feedSpeech(chunk, flush){
  sentenceBuf += chunk;
  const re = /[^。！？!?；;\n]*[。！？!?；;\n]+/g;
  let m, last = 0;
  while ((m = re.exec(sentenceBuf))) { speak(m[0]); last = re.lastIndex; }
  sentenceBuf = sentenceBuf.slice(last);
  if (flush && sentenceBuf.trim()) { speak(sentenceBuf); sentenceBuf = ""; }
}

// ---------------- 問問題 ----------------
async function ask(question){
  question = (question || "").trim();
  if (!question || !kid) return;
  if (aborter) aborter.abort();
  stopSpeaking();
  busy = true; streamDone = false; sentenceBuf = "";
  heard.textContent = question;
  bubble.textContent = "";
  setState("thinking");
  history.push({ role:"user", text:question });
  history = history.slice(-12);
  while (history.length && history[0].role !== "user") history.shift();

  aborter = new AbortController();
  let answer = "";
  try {
    const res = await fetch("/api/why/ask", {
      method:"POST", headers:{ "Content-Type":"application/json" },
      body: JSON.stringify({ kid, messages: history }), signal: aborter.signal,
    });
    if (res.status === 401) throw new Error("login");
    if (res.status === 429) throw new Error("limit");
    if (!res.ok || !res.body) throw new Error("http");
    const reader = res.body.getReader(), dec = new TextDecoder();
    let buf = "";
    for (;;) {
      const { value, done } = await reader.read();
      if (done) break;
      buf += dec.decode(value, { stream:true });
      let nl;
      while ((nl = buf.indexOf("\n")) >= 0) {
        const line = buf.slice(0, nl); buf = buf.slice(nl + 1);
        if (!line.trim()) continue;
        const msg = JSON.parse(line);
        if (msg.t) { answer += msg.t; bubble.textContent = answer; bubble.scrollTop = bubble.scrollHeight; feedSpeech(msg.t, false); }
        if (msg.error) throw new Error("api");
        if (msg.done && msg.refused && !answer) {
          answer = "這是很重要的問題，我們一起去問爸爸媽媽好不好？";
          bubble.textContent = answer; feedSpeech(answer, false);
        }
      }
    }
    feedSpeech("", true);
    if (answer) history.push({ role:"assistant", text:answer });
    else history.pop();
  } catch (e) {
    if (e.name === "AbortError") return;
    history.pop();
    const say = {
      login:"要請爸爸媽媽先登入喔！",
      limit:"今天問了好多問題，我有點累了，明天再來玩好不好？",
    }[e.message] || "哎呀，我剛剛沒想好，可以再問我一次嗎？";
    bubble.textContent = say; speak(say);
  } finally {
    busy = false; streamDone = true; aborter = null;
    if (speakingCount === 0) setState("idle");
  }
}

// ---------------- 語音辨識（按住說話） ----------------
const SR = window.SpeechRecognition || window.webkitSpeechRecognition;
const mic = $("mic");
let rec = null, recText = "", holding = false;

if (!SR) {
  mic.hidden = true;
  $("typebar").hidden = false;
  hint.textContent = "這個瀏覽器不支援語音辨識，請用打字或點上面的題目";
}

function startListen(e){
  e.preventDefault();
  if (!SR || holding) return;
  holding = true;
  if (aborter) aborter.abort();
  stopSpeaking();
  recText = "";
  heard.textContent = "";
  mic.classList.add("on"); $("micLabel").textContent = "我在聽…";
  setState("listening");
  rec = new SR();
  rec.lang = "zh-TW"; rec.interimResults = true; rec.continuous = true; rec.maxAlternatives = 1;
  rec.onresult = ev => {
    let s = "";
    for (let i = 0; i < ev.results.length; i++) s += ev.results[i][0].transcript;
    recText = s; heard.textContent = s;
  };
  rec.onerror = ev => {
    if (ev.error === "not-allowed" || ev.error === "service-not-allowed")
      hint.textContent = "請允許使用麥克風與語音辨識（設定 → Safari → 麥克風）";
  };
  rec.onend = () => {
    mic.classList.remove("on"); $("micLabel").textContent = "按住說話";
    const q = recText.trim(); rec = null;
    if (holding) holding = false;
    if (q) ask(q);
    else { setState("idle"); const s = "我沒有聽清楚，再按住按鈕說一次好嗎？"; bubble.textContent = s; speak(s); }
  };
  try { rec.start(); } catch { holding = false; }
}
function stopListen(e){
  if (e) e.preventDefault();
  if (!holding) return;
  holding = false;
  if (rec) setTimeout(() => rec && rec.stop(), 350); // 放開後稍等一下，最後一個字才不會被切掉
}
mic.addEventListener("pointerdown", startListen);
mic.addEventListener("pointerup", stopListen);
mic.addEventListener("pointercancel", stopListen);
mic.addEventListener("contextmenu", e => e.preventDefault());

// ---------------- 畫面切換 ----------------
function openChat(k){
  kid = k; history = [];
  document.body.className = k;
  $("charsvg").innerHTML = KIDS[k].svg;
  $("who").textContent = KIDS[k].who;
  $("chips").innerHTML = "";
  for (const q of KIDS[k].chips) {
    const b = document.createElement("button");
    b.className = "chip"; b.textContent = q; b.onclick = () => ask(q);
    $("chips").appendChild(b);
  }
  heard.textContent = ""; bubble.textContent = KIDS[k].greet;
  $("home").hidden = true; $("chat").hidden = false;
  setState("happy");
  setTimeout(() => { if (stage.classList.contains("happy")) setState("idle"); }, 1000);
  // iPhone 要在使用者點擊當下先講一次話，之後才允許自動朗讀
  speak(KIDS[k].greet);
}
document.querySelectorAll(".pick").forEach(b => b.onclick = () => openChat(b.dataset.kid));
$("back").onclick = () => {
  if (aborter) aborter.abort(); stopSpeaking();
  $("chat").hidden = true; $("home").hidden = false; document.body.className = "";
};
$("kbd").onclick = () => { $("typebar").hidden = !$("typebar").hidden; if (!$("typebar").hidden) $("typed").focus(); };
$("typebar").onsubmit = e => { e.preventDefault(); const v = $("typed").value; $("typed").value = ""; $("typed").blur(); ask(v); };
</script>
</body>
</html>
""";
}

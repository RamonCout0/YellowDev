// ============================================================================
//  MomFront / app.js — front-end JavaScript puro do Yellow Devil (MOM)
// ----------------------------------------------------------------------------
//  É PRODUTOR e CONSUMIDOR ao mesmo tempo, provando o desacoplamento nos dois
//  sentidos e entre DUAS linguagens/protocolos diferentes:
//    * back-end C#  -> AMQP  (porta 5672)
//    * este front   -> STOMP sobre WebSocket (porta 15674, plugin web-stomp)
//  Ambos falam com o MESMO broker RabbitMQ e as MESMAS filas.
//
//  Filas usadas:
//    /queue/devil-summons    (publica)  -> pede a invocação do boss
//    /queue/devil-particles  (consome)  -> recebe os pixels do boss
//    /queue/devil-hits       (publica)  -> registra cada tiro
// ============================================================================

// ---- Identidade desta janela ------------------------------------------------
const meuNome = 'janela-' + Math.random().toString(36).slice(2, 6);
document.getElementById('janela').textContent = meuNome;
document.title = meuNome + ' — Yellow Devil (MOM)';

// ---- Canvas / desenho -------------------------------------------------------
const SCALE = 12;                 // px por pixel do sprite
const SPRITE_W = 52, SPRITE_H = 36;
const BOSS_PX_W = SPRITE_W * SCALE; // 624
const PALETA = { Y: '#f8d800', O: '#a88000', W: '#ffffff', R: '#d82800' };

const canvas = document.getElementById('canvas');
const ctx = canvas.getContext('2d');
const audio = document.getElementById('bossTheme');
const logEl = document.getElementById('log');
const statusEl = document.getElementById('status');
const btnInvocar = document.getElementById('invocar');
const btnAtirar = document.getElementById('atirar');
const btnDevolver = document.getElementById('devolver');
const btnMudo = document.getElementById('mudo');
const divisor = document.getElementById('divisor');

// ---- Estado ----------------------------------------------------------------
let client = null;
let streamAtual = -1;      // streamId do teleporte que está sendo montado
let recebidas = 0;         // partículas recebidas neste stream
let bossCompleto = false;  // já montei o boss inteiro nesta janela?
let hpLocal = 28;          // barra de vida cosmética (a autoritativa fica no orquestrador)
const framebuffer = new Map(); // "x,y" -> cor (letra Y|O|W|R)

// ---- Log --------------------------------------------------------------------
function log(msg) {
  const hora = new Date().toLocaleTimeString();
  logEl.textContent += `[${hora}] ${msg}\n`;
  logEl.scrollTop = logEl.scrollHeight;
}

function setStatus(on) {
  statusEl.className = on ? 'on' : 'off';
  statusEl.textContent = on ? '● conectado ao broker (STOMP/ws:15674)' : '● desconectado';
}

// A trilha do chefe toca enquanto o chefe está AQUI. Saiu o boss (derrotado,
// devolvido ou teletransportado para outra janela), para a música — senão ela
// fica em loop durante o resto da apresentação.
function pararMusica() {
  audio.pause();
  audio.currentTime = 0; // o próximo INVOCAR recomeça a trilha do início
}

// ---- Efeitos sonoros (Web Audio) --------------------------------------------
// Sintetizados na hora, sem arquivo: onda quadrada + queda de frequência é
// exatamente como o NES fazia esses sons. O AudioContext só pode nascer depois
// de um gesto do usuário, por isso é criado na primeira vez que alguém atira.
let ctxAudio = null;

function bipe({ de, para, dur, tipo = 'square', vol = 0.16 }) {
  if (audio.muted) return;               // o mudo vale para tudo, não só a música
  try {
    ctxAudio = ctxAudio || new (window.AudioContext || window.webkitAudioContext)();
    const t = ctxAudio.currentTime;
    const osc = ctxAudio.createOscillator();
    const ganho = ctxAudio.createGain();
    osc.type = tipo;
    osc.frequency.setValueAtTime(de, t);
    osc.frequency.exponentialRampToValueAtTime(para, t + dur);
    ganho.gain.setValueAtTime(vol, t);
    ganho.gain.exponentialRampToValueAtTime(0.0001, t + dur); // decai, não corta seco
    osc.connect(ganho).connect(ctxAudio.destination);
    osc.start(t);
    osc.stop(t + dur + 0.02);
  } catch (e) {
    /* navegador sem Web Audio: o tiro continua funcionando, só fica mudo */
  }
}

// "Pew" do buster: agudo caindo rápido para grave.
const somTiro = () => bipe({ de: 1250, para: 180, dur: 0.11 });

// Explosão do chefe: grave, mais longo e mais sujo (dente de serra).
const somDerrota = () => bipe({ de: 420, para: 40, dur: 0.55, tipo: 'sawtooth', vol: 0.22 });

// Botões contextuais: sem o boss => só INVOCAR; com o boss => ATIRAR + DEVOLVER.
function atualizaBotoes() {
  btnInvocar.classList.toggle('hidden', bossCompleto);
  btnAtirar.classList.toggle('hidden', !bossCompleto);
  btnDevolver.classList.toggle('hidden', !bossCompleto);
  divisor.classList.toggle('hidden', !bossCompleto);
}

// ---- Desenho ----------------------------------------------------------------
function drawPixelCell(x, y, cor) {
  ctx.fillStyle = PALETA[cor] || PALETA.Y;
  ctx.fillRect(x * SCALE, y * SCALE, SCALE, SCALE);
}

// Barra de vida vertical estilo medidor de chefe de Mega Man (28 células).
function drawHealthBar() {
  const barX = 640, barW = 44, cellH = 14, gap = 1;
  const totalH = 28 * (cellH + gap) - gap;
  const y0 = Math.floor((canvas.height - totalH) / 2);
  // moldura
  ctx.fillStyle = '#101018';
  ctx.fillRect(barX - 4, y0 - 4, barW + 8, totalH + 8);
  ctx.strokeStyle = '#f8d800';
  ctx.lineWidth = 2;
  ctx.strokeRect(barX - 4, y0 - 4, barW + 8, totalH + 8);
  for (let i = 0; i < 28; i++) {
    // topo = HP perdido; base = HP restante (drena de cima para baixo)
    const cheia = i >= (28 - hpLocal);
    ctx.fillStyle = cheia ? '#f8d800' : '#33331a';
    const cy = y0 + i * (cellH + gap);
    ctx.fillRect(barX, cy, barW, cellH);
  }
}

function redrawAll() {
  ctx.clearRect(0, 0, canvas.width, canvas.height);
  for (const [chave, cor] of framebuffer) {
    const [x, y] = chave.split(',').map(Number);
    drawPixelCell(x, y, cor);
  }
  drawHealthBar();
}

// ---- Animações --------------------------------------------------------------
// 1) "shock": choque elétrico / flash de ~1s ao completar o boss.
function animShock() {
  const inicio = performance.now();
  function frame(t) {
    const e = t - inicio;
    redrawAll();
    if (e > 1000) return;
    ctx.fillStyle = `rgba(255,255,255,${0.35 * Math.random()})`;
    ctx.fillRect(0, 0, BOSS_PX_W, canvas.height);
    ctx.strokeStyle = '#bfefff';
    ctx.lineWidth = 2;
    for (let k = 0; k < 5; k++) {
      ctx.beginPath();
      let x = Math.random() * BOSS_PX_W, y = 0;
      ctx.moveTo(x, y);
      while (y < canvas.height) {
        x += (Math.random() - 0.5) * 60;
        y += 20 + Math.random() * 20;
        ctx.lineTo(x, y);
      }
      ctx.stroke();
    }
    requestAnimationFrame(frame);
  }
  requestAnimationFrame(frame);
}

// 2) "tiro": projétil atravessando o canvas até o boss, com flash de impacto.
function animTiro() {
  let x = 0;
  const y = canvas.height / 2;
  function frame() {
    redrawAll();
    ctx.fillStyle = '#8adfff';
    ctx.beginPath();
    ctx.arc(x, y, 7, 0, Math.PI * 2);
    ctx.fill();
    // rastro
    ctx.fillStyle = 'rgba(138,223,255,0.4)';
    ctx.fillRect(x - 40, y - 3, 40, 6);
    x += 45;
    if (x < BOSS_PX_W * 0.55) {
      requestAnimationFrame(frame);
    } else {
      redrawAll();
      ctx.fillStyle = 'rgba(255,255,255,0.7)';
      ctx.beginPath();
      ctx.arc(x, y, 34, 0, Math.PI * 2);
      ctx.fill();
      setTimeout(redrawAll, 90);
    }
  }
  requestAnimationFrame(frame);
}

// 3) "desintegração": os pixels somem em ondas aleatórias (não tudo de uma vez).
function animDesintegrar(aoTerminar) {
  const chaves = [...framebuffer.keys()];
  for (let i = chaves.length - 1; i > 0; i--) {
    const j = Math.floor(Math.random() * (i + 1));
    [chaves[i], chaves[j]] = [chaves[j], chaves[i]];
  }
  let idx = 0;
  const porOnda = Math.max(1, Math.ceil(chaves.length / 24));
  function frame() {
    for (let k = 0; k < porOnda && idx < chaves.length; k++, idx++) {
      framebuffer.delete(chaves[idx]);
    }
    redrawAll();
    if (idx < chaves.length) {
      requestAnimationFrame(frame);
    } else if (aoTerminar) {
      aoTerminar();
    }
  }
  requestAnimationFrame(frame);
}

// ---- Handler de partícula recebida -----------------------------------------
function onParticula(msg) {
  let p;
  try {
    p = JSON.parse(msg.body);
  } catch (e) {
    msg.ack();
    return;
  }

  // Sobra de um teleporte anterior (stream mais antigo): descarta.
  if (p.streamId < streamAtual) {
    msg.ack();
    return;
  }

  // REGRA DO TELEPORTE (protocolo por convenção, sem roteamento direcionado):
  // já tenho o boss completo e chega uma partícula chamada por OUTRA janela =>
  // o boss "vai embora": animo a desintegração e me DESCONECTO SEM dar ack.
  // A mensagem não confirmada volta para a fila e é REENTREGUE à janela que
  // chamou. Isso é o "teleportar para quem chamou" via reentrega AMQP pura.
  if (bossCompleto && p.chamadoPor !== meuNome) {
    log(`✦ TELETRANSPORTE: '${p.chamadoPor}' chamou o boss — ele está me deixando...`);
    bossCompleto = false;
    clearTimeout(timerMontagem);
    atualizaBotoes();
    animDesintegrar(() => {
      pararMusica();
      log(`✦ Boss teletransportado para '${p.chamadoPor}'. Desconectando SEM ack (a partícula volta pra fila).`);
      client.deactivate(); // sem ack => reentrega para quem chamou
    });
    return; // NÃO dá ack de propósito
  }

  // Início de um novo teleporte: reseta o framebuffer e o HP.
  if (p.streamId !== streamAtual) {
    streamAtual = p.streamId;
    recebidas = 0;
    bossCompleto = false;
    clearTimeout(timerMontagem);
    framebuffer.clear();
    hpLocal = 28;
    redrawAll();
    log(`◆ Novo teleporte (stream ${p.streamId}) chamado por '${p.chamadoPor}'.`);
  }

  // Desenha o pixel e confirma.
  framebuffer.set(p.x + ',' + p.y, p.cor);
  drawPixelCell(p.x, p.y, p.cor);
  recebidas++;
  let linha = `[${meuNome}] partícula ${p.partId} (${recebidas} recebidas)`;
  if (msg.headers.redelivered === 'true') linha += ' << REENTREGUE!';
  log(linha);
  msg.ack();

  // "Montagem terminou": ou recebeu TODAS as partículas, ou o fluxo ficou quieto.
  // Como consumidores concorrentes DIVIDEM as partículas, esta janela costuma
  // receber só uma PARTE — então não dá pra exigir bater exatamente 'total'.
  agendaFinalizacao(p.total);
}

// Decide quando o boss está "pronto" nesta janela e libera ATIRAR/DEVOLVER.
let timerMontagem = null;
function agendaFinalizacao(total) {
  if (bossCompleto) return;
  clearTimeout(timerMontagem);
  if (recebidas >= total) { finalizaMontagem(total); return; }  // caminho rápido: recebeu tudo
  timerMontagem = setTimeout(() => finalizaMontagem(total), 900); // ou: fluxo ficou quieto
}
function finalizaMontagem(total) {
  clearTimeout(timerMontagem);
  if (bossCompleto || recebidas === 0) return;
  bossCompleto = true;
  atualizaBotoes();
  log(`★ BOSS PRONTO nesta janela (${recebidas}/${total} particulas)! ATIRAR (ou ESPACO) / DEVOLVER AO SERVIDOR.`);
  animShock();
}

// ---- Atirar (botão ATIRAR ou tecla ESPAÇO) ---------------------------------
function atirar() {
  if (!bossCompleto || !client || !client.connected) return;
  somTiro();
  animTiro();
  hpLocal = Math.max(0, hpLocal - 1);
  redrawAll();
  // Front como PRODUTOR: publica o tiro (o HP autoritativo fica no orquestrador).
  client.publish({
    destination: '/queue/devil-hits',
    body: JSON.stringify({ de: meuNome, dano: 1 }),
  });
  log(`🔫 tiro publicado (devil-hits). HP local: ${hpLocal}/28.`);

  // HP zerou => o boss é derrotado, recupera a vida e volta pro servidor.
  // (O orquestrador também detecta o HP 0 e restaura o estado autoritativo.)
  if (hpLocal === 0) {
    somDerrota();
    log('☠ Boss DERROTADO! Recupera a vida e volta para o servidor...');
    bossCompleto = false;
    clearTimeout(timerMontagem);
    atualizaBotoes();
    animDesintegrar(() => {
      hpLocal = 28;
      redrawAll();
      pararMusica();
      log('↩ Boss de volta ao servidor (HP 28/28). Clique INVOCAR para chamá-lo de novo.');
    });
  }
}

// ---- Devolver o boss ao servidor (botão DEVOLVER) --------------------------
function devolver() {
  if (!bossCompleto || !client || !client.connected) return;
  // Front como PRODUTOR: avisa o servidor que está devolvendo o boss.
  client.publish({
    destination: '/queue/devil-return',
    body: JSON.stringify({ de: meuNome }),
  });
  log('↩ DEVOLVENDO o boss ao servidor (devil-return)...');
  bossCompleto = false;
  clearTimeout(timerMontagem);
  atualizaBotoes();
  animDesintegrar(() => {
    hpLocal = 28;
    redrawAll();
    pararMusica();
    log('↩ Boss devolvido ao servidor. Outra janela já pode INVOCAR.');
  });
}

// ---- Conexão STOMP ----------------------------------------------------------
function conectar(aoConectar) {
  if (client && client.connected) {
    if (aoConectar) aoConectar();
    return;
  }
  client = new StompJs.Client({
    brokerURL: 'ws://localhost:15674/ws',
    connectHeaders: { login: 'guest', passcode: 'guest' },
    // Sem reconexão automática: o teleporte se desconecta de propósito e NÃO
    // deve voltar sozinho para a fila (senão roubaria as partículas de novo).
    reconnectDelay: 0,
  });

  client.onConnect = () => {
    setStatus(true);
    log('Conectado ao broker via STOMP/WebSocket (ws://localhost:15674/ws).');
    // Espelha o BasicQos(1) do C#: prefetch=1 + ack individual => cada partícula
    // vai para UM consumidor e a reentrega fica visível.
    client.subscribe('/queue/devil-particles', onParticula, {
      ack: 'client-individual',
      'prefetch-count': '1',
    });
    if (aoConectar) aoConectar();
  };
  client.onStompError = (frame) => log('STOMP erro: ' + (frame.headers['message'] || '?'));
  client.onWebSocketClose = () => setStatus(false);
  client.onWebSocketError = () => log('Falha no WebSocket — o RabbitMQ está de pé? Rode "devil broker" e confira o plugin web_stomp (porta 15674).');

  client.activate();
}

// ---- Botão INVOCAR ----------------------------------------------------------
btnInvocar.addEventListener('click', () => {
  // audio.play() precisa ser disparado por um gesto do usuário (política de autoplay).
  audio.play().catch(() => {});
  conectar(() => {
    client.publish({
      destination: '/queue/devil-summons',
      body: JSON.stringify({ chamadoPor: meuNome }),
    });
    log(`▶ INVOQUEI o boss (publiquei em devil-summons como '${meuNome}').`);
  });
});

// ---- Botões ATIRAR e DEVOLVER ----------------------------------------------
btnAtirar.addEventListener('click', atirar);
btnDevolver.addEventListener('click', devolver);

// ---- Mudo -------------------------------------------------------------------
// Usa audio.muted, e não pause(): assim o mudo é uma preferência independente do
// ciclo do boss (pararMusica() continua mandando no play/pause).
function alternaMudo() {
  audio.muted = !audio.muted;
  btnMudo.textContent = audio.muted ? '🔇' : '🔊';
  btnMudo.title = audio.muted
    ? 'Música desligada — clique para ligar (tecla M)'
    : 'Música ligada — clique para desligar (tecla M)';
}
btnMudo.addEventListener('click', alternaMudo);

// Teclas: ESPAÇO = atirar · M = mudo.
window.addEventListener('keydown', (e) => {
  if (e.code === 'KeyM') { alternaMudo(); return; }
  if (e.code !== 'Space') return;
  e.preventDefault();
  atirar();
});

// ---- Estado inicial ---------------------------------------------------------
redrawAll();
setStatus(false);
atualizaBotoes();
log(`Janela '${meuNome}' pronta. Clique em "INVOCAR O BOSS" para começar.`);

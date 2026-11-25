# 🐉 Project Chimera — Adaptive Boss AI via Hybrid Reinforcement Learning (PPO + FSM)

🌐 **Language / Idioma:** [English](#-english) | [Español](#-español)

---

## 🇬🇧 English

[![Unity](https://img.shields.io/badge/Unity-6%20LTS%20(6000.4)-black?style=for-the-badge&logo=unity)](https://unity.com/)
[![ML-Agents](https://img.shields.io/badge/ML--Agents-v1.1.0-blue?style=for-the-badge&logo=unity)](https://github.com/Unity-Technologies/ml-agents)
[![PyTorch](https://img.shields.io/badge/PyTorch-2.2.2%20CUDA-EE4C2C?style=for-the-badge&logo=pytorch)](https://pytorch.org/)
[![Python](https://img.shields.io/badge/Python-3.10-3776AB?style=for-the-badge&logo=python)](https://www.python.org/)

Tactical 3D boss-fight prototype built in **Unity 6 LTS (URP)** and **Python**. **Project Chimera** solves the predictability of traditional static boss patterns through a **3-Layer Hybrid AI Architecture**: a real-time sliding-window behavioral profiler, a **Proximal Policy Optimization (PPO)** tactical decision network, and a deterministic Finite State Machine (FSM) that guarantees readable telegraphs and fair hitboxes.

| Player Victory (Out-of-Distribution Perfect Parries) | Boss Victory (Adaptive Counter-Attack Pressure) |
| :---: | :---: |
| ![Player Victory Demo](01demo-1x.gif) | ![Boss Victory Demo](02demo-1x.gif) |

*Figure 1: Trained ONNX model (`ChimeraBoss.onnx`) running at `time_scale = 1` in `Inference Only` mode against human input.*

---

### ⚙️ Environment Setup & Hyperparameters (YAML)

Trained across **8 parallel octagonal arenas** at `time_scale = 20` on local hardware (AMD Ryzen 7 3700X + NVIDIA RTX 4060 CUDA), completing **500,000 steps in 22m 48s** against three parameterized heuristic player archetypes (`40% AggressiveRusher`, `40% EvasiveKiter`, `20% BalancedHybrid`). Parameters configured in `Assets/_Project/ML-Agents/Config/chimera_boss.yaml`:

| Hyperparameter | Configured Value | Technical Rationale |
| :--- | :--- | :--- |
| **Trainer / Network** | `PPO` / MLP (`2 × 128`) | Two-layer neural network with automatic observation normalization (`normalize: true`). |
| **Batch Size** | `256` | Stabilizes gradient updates across parallel combat episodes. |
| **Buffer Size** | `4096` | Collects 16 mini-batches of combat transitions before each policy update. |
| **Learning Rate** | `0.0003` (Linear Decay) | Smooth step-size annealing to prevent late-stage policy oscillation. |
| **Beta (Entropy)** | `0.015` | Encourages tactical exploration across movement and counter-attack combinations. |
| **Epsilon (PPO Clip)** | `0.2` | Bounds policy ratio updates to prevent catastrophic performance collapse. |
| **Lambda / Gamma** | `0.95` / `0.99` | Generalized Advantage Estimation (GAE) balancing immediate hits and episode victory. |
| **Time Horizon** | `128` | Captures multi-second telegraph, punish, and recovery sequences. |

---

### 📐 3-Layer Hybrid AI & Combat Architecture

![Arena & Layer 1 Telemetry HUD](arena-preview.png)
*Figure 2: Octagonal combat arena (`BossArena_Main`) featuring real-time Layer 1 behavioral telemetry overlay (`12s` sliding window) and predictive projectile volleys.*

#### 1. Layer 1 — Real-Time Behavioral Telemetry (`PlayerBehaviorProfiler.cs`)
Samples player actions and spatial trajectories every `0.10s` across a `12.0s` sliding window to extract **6 normalized behavioral features**:
* **Melee Aggression Ratio** $[0, 1]$ & **Ranged Pressure Ratio** $[0, 1]$.
* **Dash Frequency** $[0, 1]$ & **Perfect Parry Success Rate** $[0, 1]$.
* **Dodge Lateral Bias** $[-1, +1]$: Tracks directional strafing and dash tendencies relative to the boss, feeding directly into Layer 3's predictive aim solver.
* **Normalized Average Distance** $[0, 1]$ relative to the `20m` arena diameter.

#### 2. Layer 2 — Tactical Decision Policy (`ChimeraBossAgent.cs`)
* **Continuous Observation Space (24 Variables):**
  * Boss normalized HP, posture, Phase 2 flag, and 5-state One-Hot FSM encoding (`8` variables).
  * Player normalized HP, posture, `IsDashing`, and `IsGuarding` flags (`4` variables).
  * Normalized distance, local relative direction $(X, Z)$, and local relative player velocity $(X, Z)$ (`5` variables).
  * Boss normalized radial distance from arena center (`1` variable).
  * Layer 1 behavioral telemetry vector (`6` variables).
* **Masked Discrete Action Space ($6 \times 5 = 30$ Combinations):** Uses `IDiscreteActionMask` to lock inputs during `Telegraph`, `ActiveHitbox`, `Recovery`, and `Staggered` states:
  * **Branch 0 (Locomotion):** `0` Hold, `1` Advance, `2` Retreat, `3` StrafeLeft, `4` StrafeRight, `5` MicroDash (invulnerable i-frame dodge).
  * **Branch 1 (Counter-Attack):** `0` None, `1` GapCloserLunge, `2` ShockwaveRing, `3` LeadShotVolley, `4` DelayedParrySlam.
* **Calibrated Reward Shaping Function ($R$):**
  $$R = R_{\text{dmg}} + R_{\text{dash}} + R_{\text{posture}} + R_{\text{range}} + R_{\text{step}} + R_{\text{terminal}}$$
  * **Damage Exchange:** $+0.025 \cdot \Delta\text{HP}_{\text{player}}$ dealt (up to $+2.50$) and $-0.003 \cdot \Delta\text{HP}_{\text{boss}}$ taken.
  * **Tactical MicroDash:** $+0.18$ when evading incoming projectiles ($\le 6.0\text{ m}$) or melee pressure ($\le 3.5\text{ m}$); $-0.06$ if wasted without threat.
  * **Posture & Parry:** $+0.55$ on breaking player posture, $-0.35$ on boss staggered, $-0.15$ when parried.
  * **Range Discipline & Time:** $-0.10$ to $-0.12$ penalty for out-of-range attacks; $-0.001$ per step.
  * **Terminal Outcomes:** $+2.50$ (Player defeated), $-2.00$ (Boss defeated), $-0.50$ (Timeout).

#### 3. Layer 3 — Deterministic Combat Execution (`BossCombatFSM.cs`)
Executes 4 distinct counter-attacks (`GapCloserLunge`, unparriable `360°` `ShockwaveRing`, velocity-predicted `LeadShotVolley`, and feint-based `DelayedParrySlam`) with ground telegraphs, procedural URP VFX (`CombatVFX.cs`), and **Phase 2 escalation** ($\le 50\%$ HP) unlocking 5-projectile fans and chained 2-attack combos.

---

### 📊 Telemetry & Quantitative Analysis (TensorBoard)

Comparison between baseline training (`ChimeraBoss_v1`) and the reward/kinematic-calibrated run (`ChimeraBoss_v2`) across **500,000 steps**:

| Metric (500k Steps) | `ChimeraBoss_v1` (Baseline) | `ChimeraBoss_v2` (Final) | Technical Interpretation |
| :--- | :---: | :---: | :--- |
| **Cumulative Reward (Smoothed)** | $+0.5648$ *(peak +1.0396)* | **$+2.1399$** *(peak +2.4066)* | **+278.9% gain**; escaped passive local optima before 70k steps. |
| **Mean Episode Length** | $147.75$ steps | **$168.75$ steps** | Sustained tactical exchanges with decisive Phase 2 finishes. |
| **Policy Loss** | $0.0478$ | **$0.0480$** | Bounded policy updates without gradient instability. |
| **Inference CPU Frame Time** | — | **$0.22\text{ ms}$** *(Unity Profiler)* | Well within the $16.6\text{ ms}$ ($60\text{ FPS}$) runtime budget. |

#### Key Experimental Finding (Sim-to-Human Generalization)
In real-time `1x` evaluation, `ChimeraBoss_v2` achieved a **100% win rate against all three training archetypes** (*Rusher*, *Kiter*, *Hybrid*), dynamically punishing lateral kiting with `LeadShotVolley` and turtle blocking with `ShockwaveRing`. However, a human player defeated the boss by chaining **Perfect Parries** (`0.24s` guard window reflecting $+35$ posture damage)—an out-of-distribution mechanic absent from the synthetic training bots.

### 📈 Convergence Plots

![TensorBoard Cumulative Reward](tb-reward.png)
*Figure 3: `Environment/Cumulative Reward` comparing `ChimeraBoss_v1` ($+0.5648$) against `ChimeraBoss_v2` ($+2.1399$).*

![TensorBoard Episode Length](tb-length.png)
*Figure 4: `Environment/Episode Length` stabilizing around $168.75$ steps across 500k steps.*

![TensorBoard Policy Loss](tb-policy-loss.png)
*Figure 5: `Losses/Policy Loss` demonstrating smooth PPO convergence around $0.048$.*

---

### 📦 Local Setup & Training

```bash
# 1. Clone repository
git clone [https://github.com/MarketablePlushiee/project-chimera-adaptive-boss.git](https://github.com/MarketablePlushiee/project-chimera-adaptive-boss.git)
cd project-chimera-adaptive-boss

# 2. Create Python 3.10 virtual environment & install dependencies
python -m venv venv_chimera
venv_chimera\Scripts\activate
pip install mlagents==1.1.0 torch==2.2.2

# 3. Start training (open BossArena_Main in Unity 6 and press Play when prompted)
mlagents-learn Assets/_Project/ML-Agents/Config/chimera_boss.yaml --run-id=ChimeraBoss_v2 --time-scale=20
```

### 👨‍💻 Author & Contact

* **Iván Latrille Brito** — [ivan.latrille1@gmail.com](mailto:ivan.latrille1@gmail.com)
* **Portfolio:** [marketableplushiee.github.io/Portafolio](https://marketableplushiee.github.io/Portafolio/)

---
---

## 🇪🇸 Español

[![Unity](https://img.shields.io/badge/Unity-6%20LTS%20(6000.4)-black?style=for-the-badge&logo=unity)](https://unity.com/)
[![ML-Agents](https://img.shields.io/badge/ML--Agents-v1.1.0-blue?style=for-the-badge&logo=unity)](https://github.com/Unity-Technologies/ml-agents)
[![PyTorch](https://img.shields.io/badge/PyTorch-2.2.2%20CUDA-EE4C2C?style=for-the-badge&logo=pytorch)](https://pytorch.org/)
[![Python](https://img.shields.io/badge/Python-3.10-3776AB?style=for-the-badge&logo=python)](https://www.python.org/)

Prototipo de combate táctico 3D desarrollado en **Unity 6 LTS (URP)** y **Python**. **Project Chimera** resuelve la previsibilidad de los jefes basados en patrones estáticos mediante una **Arquitectura Híbrida de IA en 3 Capas**: un perfilador conductual en ventana deslizante, una red neuronal entrenada con **Proximal Policy Optimization (PPO)** para toma de decisiones tácticas y una Máquina de Estados Finitos (FSM) determinista que garantiza telegrafías visuales justas y *hitboxes* precisas.

| Victoria del Jugador (Parry Perfecto Out-of-Distribution) | Victoria del Jefe (Presión y Contra-ataque Adaptativo) |
| :---: | :---: |
| ![Demo Victoria Jugador](01demo-1x.gif) | ![Demo Victoria Jefe](02demo-1x.gif) |

*Figura 1: Modelo ONNX entrenado (`ChimeraBoss.onnx`) ejecutándose en tiempo real (`time_scale = 1`) en modo `Inference Only` frente a un jugador humano.*

---

### ⚙️ Configuración del Entorno e Hiperparámetros (YAML)

Entrenado en **8 arenas octogonales paralelas** a `time_scale = 20` en hardware local (AMD Ryzen 7 3700X + NVIDIA RTX 4060 CUDA), completando **500.000 pasos en 22 min 48 s** frente a tres arquetipos heurísticos parametrizados (`40% AggressiveRusher`, `40% EvasiveKiter`, `20% BalancedHybrid`). Parámetros definidos en `Assets/_Project/ML-Agents/Config/chimera_boss.yaml`:

| Hiperparámetro | Valor Configurado | Justificación Técnica |
| :--- | :--- | :--- |
| **Algoritmo / Red** | `PPO` / MLP (`2 × 128`) | Perceptrón multicapa de 2 capas ocultas con normalización automática de entradas (`normalize: true`). |
| **Batch Size** | `256` | Estabiliza las actualizaciones de gradiente entre episodios paralelos de combate. |
| **Buffer Size** | `4096` | Recopila 16 mini-lotes de transiciones antes de cada actualización de política. |
| **Learning Rate** | `0.0003` (Decaimiento lineal) | Reduce progresivamente el tamaño de paso para evitar oscilaciones tardías. |
| **Beta (Entropía)** | `0.015` | Fomenta la exploración táctica entre desplazamientos y contra-ataques. |
| **Epsilon (PPO Clip)** | `0.2` | Limita la magnitud de cambio de la política para prevenir colapsos de rendimiento. |
| **Lambda / Gamma** | `0.95` / `0.99` | Estimación de ventaja generalizada (GAE) equilibrando impactos inmediatos y victoria final. |
| **Time Horizon** | `128` | Captura secuencias completas de telegrafía, castigo y recuperación. |

---

### 📐 Arquitectura Híbrida de IA en 3 Capas y Combate

![Arena y HUD de Telemetría Capa 1](arena-preview.png)
*Figura 2: Arena octogonal de combate (`BossArena_Main`) mostrando el panel HUD de telemetría en tiempo real de Capa 1 (ventana de `12s`) y ráfagas de proyectiles.*

#### 1. Capa 1 — Perfilado Conductual en Tiempo Real (`PlayerBehaviorProfiler.cs`)
Muestrea acciones y trayectorias del jugador cada `0.10s` dentro de una ventana deslizante de `12.0s` para extraer **6 métricas normalizadas**:
* **Ratio de Agresión Melee** $[0, 1]$ y **Presión a Distancia** $[0, 1]$.
* **Frecuencia de Dash** $[0, 1]$ y **Tasa de Éxito de Parry Perfecto** $[0, 1]$.
* **Sesgo Lateral de Esquive (`DodgeLateralBias`)** $[-1, +1]$: Detecta la tendencia direccional de desplazamiento lateral y esquives del jugador respecto al jefe, alimentando directamente la puntería predictiva de Capa 3.
* **Distancia Media Normalizada** $[0, 1]$ respecto al diámetro de `20m` de la arena.

#### 2. Capa 2 — Política de Decisión Táctica (`ChimeraBossAgent.cs`)
* **Espacio de Observación Continuo (24 Variables):**
  * Vida normalizada, postura, indicador de Fase 2 y codificación One-Hot de los 5 estados de la FSM del Jefe (`8` variables).
  * Vida normalizada, postura y banderas `IsDashing` / `IsGuarding` del Jugador (`4` variables).
  * Distancia normalizada, dirección relativa local $(X, Z)$ y velocidad relativa local $(X, Z)$ del Jugador (`5` variables).
  * Proximidad radial normalizada del Jefe respecto al centro de la arena (`1` variable).
  * Vector de telemetría conductual de Capa 1 (`6` variables).
* **Espacio de Acciones Discretas Enmascaradas ($6 \times 5 = 30$ Combinaciones):** Utiliza `IDiscreteActionMask` para bloquear comandos mientras el jefe ejecuta estados ocupados (`Telegraph`, `ActiveHitbox`, `Recovery`, `Staggered`):
  * **Rama 0 (Locomoción):** `0` Mantener, `1` Avanzar, `2` Retroceder, `3` Desplazar Izquierda, `4` Desplazar Derecha, `5` MicroDash (esquive con *i-frames*).
  * **Rama 1 (Contra-ataque):** `0` Ninguno, `1` GapCloserLunge, `2` ShockwaveRing, `3` LeadShotVolley, `4` DelayedParrySlam.
* **Función de Recompensa Calibrada ($R$):**
  $$R = R_{\text{dmg}} + R_{\text{dash}} + R_{\text{posture}} + R_{\text{range}} + R_{\text{step}} + R_{\text{terminal}}$$
  * **Intercambio de Daño:** $+0.025 \cdot \Delta\text{HP}_{\text{jugador}}$ infligido (hasta $+2.50$) y $-0.003 \cdot \Delta\text{HP}_{\text{jefe}}$ recibido.
  * **MicroDash Táctico:** $+0.18$ al esquivar proyectiles entrantes ($\le 6.0\text{ m}$) o presión melee ($\le 3.5\text{ m}$); $-0.06$ si se gasta sin amenaza cercana.
  * **Postura y Parry:** $+0.55$ al romper postura del jugador, $-0.35$ al quedar aturdido (`Staggered`), $-0.15$ al ser desviado por *parry*.
  * **Control de Rango y Tiempo:** Penalización de $-0.10$ a $-0.12$ por ataques lanzados fuera de rango efectivo; $-0.001$ por paso.
  * **Recompensas Terminales:** $+2.50$ (Victoria del Jefe), $-2.00$ (Derrota del Jefe), $-0.50$ (Límite de tiempo).

#### 3. Capa 3 — Ejecución Determinista de Combate (`BossCombatFSM.cs`)
Ejecuta 4 contra-ataques diferenciados (`GapCloserLunge`, onda expansiva `360°` imparable `ShockwaveRing`, ráfaga predictiva `LeadShotVolley` y amague retardado `DelayedParrySlam`) con telegrafías en el suelo, VFX procedurales en URP (`CombatVFX.cs`) y **escalado de Fase 2** ($\le 50\%$ HP) que habilita abanicos de 5 proyectiles y combos encadenados de 2 golpes.

---

### 📊 Telemetría y Análisis Cuantitativo (TensorBoard)

Comparativa entre el entrenamiento base (`ChimeraBoss_v1`) y la iteración calibrada (`ChimeraBoss_v2`) tras **500.000 pasos**:

| Métrica (500k pasos) | `ChimeraBoss_v1` (Base) | `ChimeraBoss_v2` (Final) | Interpretación Técnica |
| :--- | :---: | :---: | :--- |
| **Recompensa Acumulada (Suavizada)** | $+0.5648$ *(pico +1.0396)* | **$+2.1399$** *(pico +2.4066)* | **Incremento de +278,9%**; supera mínimos locales antes de los 70k pasos. |
| **Duración Media Episodio** | $147.75$ pasos | **$168.75$ pasos** | Intercambios tácticos sostenidos con cierres decisivos en Fase 2. |
| **Policy Loss** | $0.0478$ | **$0.0480$** | Actualizaciones de política acotadas y estables sin explosión de gradientes. |
| **Tiempo CPU por Frame (Inferencia)** | — | **$0.22\text{ ms}$** *(Unity Profiler)* | Cumple holgadamente el presupuesto de $16.6\text{ ms}$ ($60\text{ FPS}$). |

#### Hallazgo Experimental (Generalización Sim-to-Human)
En evaluación en tiempo real (`1x`), `ChimeraBoss_v2` obtuvo un **100% de victorias frente a los tres arquetipos de entrenamiento** (*Rusher*, *Kiter*, *Hybrid*), castigando desplazamientos laterales con `LeadShotVolley` y bloqueos pasivos con `ShockwaveRing`. Sin embargo, un jugador humano logró derrotarlo encadenando **Parries Perfectos** (ventana de $0.24\text{ s}$ que devuelve $+35$ de daño de postura), una mecánica *out-of-distribution* ausente en los bots sintéticos.

### 📈 Gráficos de Convergencia

![Recompensa Acumulada TensorBoard](tb-reward.png)
*Figura 3: `Environment/Cumulative Reward` comparando `ChimeraBoss_v1` ($+0.5648$) frente a `ChimeraBoss_v2` ($+2.1399$).*

![Duración de Episodio TensorBoard](tb-length.png)
*Figura 4: `Environment/Episode Length` estabilizándose en torno a $168.75$ pasos tras 500k pasos.*

![Pérdida de Política TensorBoard](tb-policy-loss.png)
*Figura 5: `Losses/Policy Loss` demostrando convergencia suave de PPO alrededor de $0.048$.*

---

### 📦 Instalación y Entrenamiento Local

```bash
# 1. Clonar el repositorio
git clone [https://github.com/MarketablePlushiee/project-chimera-adaptive-boss.git](https://github.com/MarketablePlushiee/project-chimera-adaptive-boss.git)
cd project-chimera-adaptive-boss

# 2. Crear entorno virtual Python 3.10 e instalar dependencias
python -m venv venv_chimera
venv_chimera\Scripts\activate
pip install mlagents==1.1.0 torch==2.2.2

# 3. Iniciar entrenamiento (abrir BossArena_Main en Unity 6 y presionar Play al solicitarlo)
mlagents-learn Assets/_Project/ML-Agents/Config/chimera_boss.yaml --run-id=ChimeraBoss_v2 --time-scale=20
```

### 👨‍💻 Autor y Contacto

* **Iván Latrille Brito** — [ivan.latrille1@gmail.com](mailto:ivan.latrille1@gmail.com)
* **Portafolio:** [marketableplushiee.github.io/Portafolio](https://marketableplushiee.github.io/Portafolio/)
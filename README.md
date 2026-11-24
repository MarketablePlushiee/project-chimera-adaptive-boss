# Project Chimera: Adaptive Boss Arena

Prototipo técnico de programación de gameplay e inteligencia artificial adaptativa desarrollado en **Unity 6 (URP)**, **C#** y **Unity ML-Agents (PPO)**.

## Arquitectura Híbrida de 3 Capas
1. **PlayerBehaviorProfiler:** Telemetría en ventana deslizante que analiza en tiempo real los hábitos del jugador (rango preferido, sesgo lateral de esquive, índice de agresión, dependencia de parry y predictibilidad).
2. **BossBrainAgent (ML-Agents PPO):** Red neuronal que observa su estado, el del jugador y las 5 métricas de telemetría para elegir posicionamiento y contra-medidas tácticas.
3. **BossCombatFSM:** Máquina de estados determinista (`Locomotion -> Telegraph -> ActiveHitbox -> Recovery -> Staggered`) que ejecuta ataques telegráficos justos.

## Requisitos y Ejecución
- **Motor:** Unity `6000.4.11f1` (Universal Render Pipeline)
- **Paquetes:** `com.unity.ml-agents` (4.0.3), `com.unity.inputsystem` (1.19.0)
- **Escena principal:** `Assets/_Project/Scenes/BossArena_Main.unity`

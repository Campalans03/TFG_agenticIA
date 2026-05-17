# TFG AgenticIA

Treball de Fi de Grau sobre **comunicació emergent entre agents** mitjançant aprenentatge per reforç multi-agent. El projecte implementa un entorn **Speaker-Listener** a Unity amb ML-Agents.

L'objectiu és estudiar com dos agents independents poden aprendre des de zero un protocol de comunicació discret per resoldre una tasca cooperativa que cap dels dos pot resoldre per separat.

## L'entorn

L'escenari conté tres botons amb un color (`Red`, `Green`, `Blue`) i una forma (`Square`, `Circle`, `Triangle`) assignats aleatòriament a cada episodi. Hi ha una regla — un parell `(color, forma)` — que determina quin botó és el correcte.

- **Speaker**: observa només la regla (`targetColor`, `targetShape`), no veu l'escena. Emet **un sol token discret** d'un vocabulari de mida `vocabSize` i queda en silenci la resta de l'episodi.
- **Listener**: observa l'escena (els tres botons, els seus colors i formes detectats via raycasts, la seva pròpia posició i velocitat) i el token emès pel Speaker. No coneix la regla. Es mou pel món 3D i ha de prémer el botó correcte.

La recompensa és **compartida**: ambdós agents reben `+3.0` si el Listener prem el botó correcte, `-1.0` si s'equivoca o es queda sense temps, i una petita penalització per pas (`-0.005`) per fomentar episodis curts. Cap dels dos pot resoldre la tasca sense l'altre, de manera que la comunicació ha d'emergir per necessitat.

### Restricció de vocabulari

`vocabSize` ha de ser **≥ 9** (3 colors × 3 formes). Amb un vocabulari menor, pel principi del colomar, el Speaker no pot codificar de manera no ambigua totes les regles possibles.

## Estructura del projecte

```
Assets/Scripts/Speaker-Listener/
├── SpeakerAgent.cs          # Emet un token per episodi
├── ListenerAgent.cs         # Es mou i prem botons segons el token
├── EnvironmentManager.cs    # Orquestra episodis, reset, recompenses, mètriques
├── ButtonController.cs      # Visualització i lògica de cada botó
├── TargetIndicator.cs       # Indicador visual de l'objectiu (debug)
├── configuration/           # Configuracions YAML d'entrenament
│   ├── config_v1.yaml
│   ├── config_v2.yaml
│   ├── configNoSpeaker.yaml # Baseline sense canal de comunicació
│   └── results/             # Checkpoints i logs de TensorBoard
└── Curriculum Learning/
    ├── config_curriculum.yaml  # Currículum de 2 lliçons (2 botons → 3 botons)
    └── results/
```

## Aprenentatge curricular

L'entrenament directe amb 3 botons resulta inestable. Per facilitar l'emergència del llenguatge, s'utilitza un **currículum progressiu** controlat pel paràmetre d'entorn `active_buttons`:

1. **Lliçó 1 — 2 botons**: la tasca és més senzilla i els agents aprenen primer una correspondència bàsica regla → token.
2. **Lliçó 2 — 3 botons**: un cop el Listener supera el llindar de recompensa mitjana (`1.5`), s'activa la lliçó completa.

El plateau a recompensa mitjana `0.8` correspon a l'equilibri de Nash de "no comunicar" en la variant de 2 botons (50% d'encerts aleatoris amb compensació de pas), i és el senyal que els agents encara no han descobert el canal.

## Mètriques registrades

A `TensorBoard` es registren:

- `Speaker/EmittedToken`: distribució dels tokens emesos. Si col·lapsa a un sol valor → vocabulari mort.
- `Speaker/Token_{i}/SuccessRate`: taxa d'èxit condicionada a cada token.
- `Rule/{Color}_{Shape}/SuccessRate`: taxa d'èxit per cada regla.
- `Mapping/{Color}_{Shape}/Token`: matriu emergent regla → token per analitzar el llenguatge après off-line.
- `Listener/AccuracyRate`, `Listener/PressAttempts`: mètriques de comportament del Listener.
- `Curriculum/ActiveButtons`: lliçó activa.

## Requisits

- **Unity** 6.x (o la versió declarada al `ProjectSettings`)
- **com.unity.ml-agents** (paquet de Unity)
- **mlagents** (Python, costat entrenador) — versió compatible amb el paquet de Unity

## Com entrenar

Des de l'arrel del projecte, amb l'entorn Python de ML-Agents activat:

```bash
# Entrenament amb currículum (recomanat)
mlagents-learn "Assets/Scripts/Speaker-Listener/Curriculum Learning/config_curriculum.yaml" \
               --run-id=ExperimentX --train

# Entrenament directe a 3 botons
mlagents-learn Assets/Scripts/Speaker-Listener/configuration/config_v2.yaml \
               --run-id=ExperimentY --train

# Baseline sense canal de Speaker
mlagents-learn Assets/Scripts/Speaker-Listener/configuration/configNoSpeaker.yaml \
               --run-id=Baseline --train
```

Després obre l'escena d'entrenament a Unity i prem **Play** quan la consola mostri `Listening on port 5004`.

## Inferència

Un cop entrenat, copia els fitxers `.onnx` resultants de `results/<run-id>/` a una carpeta dins `Assets/` i assigna'ls al component `BehaviorParameters` dels prefabs `Speaker` i `Listener`. El component `ModelAutoAssigner` (a `Assets/Scripts/Debbuger/`) automatitza aquesta assignació per a escenes amb múltiples instàncies de l'entorn.

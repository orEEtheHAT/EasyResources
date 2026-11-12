# Basics — first run

**Compatibility:** EasyResources 0.3.0+. No prerequisite sample imports.

Open **ResourceBasics.unity** and press **Play**. The example runs automatically; no player, input bindings or extra packages are needed. Expect **13 PASS** lines and these final totals:

| Container | Wood | Stone | Reserved |
|---|---:|---:|---:|
| A | 6 | 3 | 0 |
| B | 3 | 1 | 0 |

The scene has one **EasyResources Basics** root with a **Resource Basics Demo**, a camera, and two children with **Resource Container** components. Outside Play, A starts with 10 Wood / 5 Stone; B starts with 3 Wood / 1 Stone. Both use the same Wood and Stone assets. **Building Cost.asset** contains 4 Wood + 2 Stone.

Read `ResourceBasicsDemo.cs` for short examples of immediate payment, competing reservations, cancellation, repeated confirmation and snapshot restoration. **Run Checks** in that component's context menu repeats the sequence during Play.

To reproduce the setup, create a **Resource** through **Create > EasyResources > Resource**, add **Resource Container** to an empty GameObject, then add that asset and its Amount to **Initial Contents**. For another ready copy use **Tools > EasyResources > Create Basics Scene**; save the current scene if prompted.

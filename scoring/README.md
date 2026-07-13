# scoring/

Diff tools that score pipeline output against the hand-labeled fixtures. Simple by design — the
golden sets in `fixtures/` are the load-bearing artifacts here, not this code.

- `score_gate1.py` — element recall, element precision, hierarchy correctness (T1.11)
- `score_gate2.py` — FAR, auto-accept rate, missing-recall, split by sprite type (T2.6)
- `report.md` — Gate 1 + Gate 2 readout and go/no-go recommendation (T2.8). Written by hand off
  both scorers' real output, not generated - covers the two risk gates only; will need a pass once
  Week 3's assembled `.prefab` exists to cover the Definition of Done's remaining item.

# scoring/

Diff tools that score pipeline output against the hand-labeled fixtures. Simple by design — the
golden sets in `fixtures/` are the load-bearing artifacts here, not this code.

- `score_gate1.py` — element recall, element precision, hierarchy correctness (T1.11)
- `score_gate2.py` — FAR, auto-accept rate, missing-recall, split by sprite type (T2.6)
- `report.md` — generated go/no-go readout (T3.6)

Not yet implemented — Week 1 / Week 2 tasks in the implementation plan.

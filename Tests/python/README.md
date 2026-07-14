# Python pipeline regression tests

These tests are fully offline and use only Python's standard library. They
cover the editorial gates, topic-evidence rules, writer response
normalization, GitHub Release material ordering, reusable drafts, atomic
publication, and minimum coverage.

Run from the repository root:

```powershell
python -m unittest discover -s Tests/python -p "test_*.py" -v
```

The same command works on GitHub Actions runners.

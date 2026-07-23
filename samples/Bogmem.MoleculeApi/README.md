# Molecule capability memory API

This sample wraps BogMem in a small HTTP service that remembers structured
human-protein records and answers two different kinds of question:

- Free-text retrieval ranks records that might answer a question.
- Capability retrieval returns only molecules containing every requested
  controlled-vocabulary function.

That separation is intentional. Similarity is useful for candidate generation,
but it does not prove an exact `AND` condition.

## Run the self-checking demo

```bash
dotnet run --project samples/Bogmem.MoleculeApi -- --demo
```

The demo uses a temporary palace and verifies these results:

- `DNA repair` AND `homologous recombination` → BRCA1 and RAD51
- `DNA damage response` AND `cell cycle checkpoint signaling` → ATM

## Run the API

```bash
export BOGMEM_MOLECULE_PALACE="$HOME/.bogmem/molecule-api"
dotnet run --project samples/Bogmem.MoleculeApi
```

Then query it:

```bash
curl --get http://localhost:5000/molecules/capabilities \
  --data-urlencode "function=DNA repair" \
  --data-urlencode "function=homologous recombination"

curl --get http://localhost:5000/molecules/search \
  --data-urlencode "q=chromatin enzyme recovery from DNA damage"

curl http://localhost:5000/functions
```

ASP.NET may select a different development port; use the listening URL printed
at startup.

`MoleculeMemoryService` is the integration boundary worth copying. Each molecule
is serialized into one drawer and replaced through a stable
`molecule://human/{symbol}` source. BogMem owns persistence and ranked candidate
retrieval. The application owns record validation, controlled vocabulary, and
the exact all-functions predicate.

The four seed records are compact educational summaries linked to their NCBI
Gene entries:

- [BRCA1](https://www.ncbi.nlm.nih.gov/gene/672)
- [RAD51](https://www.ncbi.nlm.nih.gov/gene/5888)
- [ATM](https://www.ncbi.nlm.nih.gov/gene/472)
- [PARP1](https://www.ncbi.nlm.nih.gov/gene/142)

This sample is an integration demonstration, not medical or clinical guidance.

# Native protocol fixtures

`vectors.json` contains fixed protocol test vectors using synthetic keys and
the two default PK3 assets. It contains no user prod.keys or real room capture.
The C# checks cover LDN advertisements, key derivation, player and trainer-card
blocks, NI, PK3 wire encoding, Pia encryption and compression. Changes to these
vectors require independent verification of the expected protocol bytes.

The optional real trade replay stays in `local/native-tests/engine.json`; it is
never packaged here. Its absence is reported as a skipped replay, not a pass.

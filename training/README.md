# Training the rock model

The rock model learns, from places where HiRISE shows the real rocks, how many rocks there are compared with what
Mars Trek shows. Run these in this folder (Python 3.10+, `pip install numpy scipy rasterio pyproj onnxruntime certifi`):

1. `python collect_training_data.py` downloads HiRISE areas and the Mars Trek layers (a few hours; safe to stop and
   run again). `--sites 20` for a quick test.
2. `python build_dataset.py` finds the real rocks with RockBoxNet and makes the lessons (safe to stop and run again).
3. `python train_rock_model.py` (needs `pip install torch`) tests the model on HiRISE sites it never saw, then saves
   `../models/rock_density_v1.onnx` and `.json`. Rebuild the app to include the new model.

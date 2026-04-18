import sys
import os
from onnxruntime.quantization import quantize_dynamic, QuantType

if len(sys.argv) < 3:
    print("Usage: python convert_int8.py <model.onnx> <output_folder>")
    sys.exit(1)

model = sys.argv[1]
output_folder = sys.argv[2]

if not os.path.exists(model):
    print(f"Error: File not found: {model}")
    sys.exit(1)

# Criar pasta de saida se nao existir
os.makedirs(output_folder, exist_ok=True)

base_name = os.path.basename(model).replace(".onnx", "_int8.onnx")
output = os.path.join(output_folder, base_name)

print(f"Converting: {os.path.basename(model)} -> models_opmized/{base_name}")
print("This may take a few minutes...")

quantize_dynamic(model, output, weight_type=QuantType.QUInt8)

if os.path.exists(output):
    original_size = os.path.getsize(model)
    int8_size = os.path.getsize(output)
    reduction = (1 - int8_size / original_size) * 100
    print(f"Converted successfully!")
    print(f"Original: {original_size / 1024 / 1024:.1f} MB")
    print(f"INT8:     {int8_size / 1024 / 1024:.1f} MB ({reduction:.0f}% smaller)")
    print(f"Saved to: {output}")
else:
    print("Error: Output file was not created")
    sys.exit(1)

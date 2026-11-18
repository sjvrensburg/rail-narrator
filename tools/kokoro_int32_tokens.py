"""Rewrites a Kokoro ONNX export so its 'tokens' input is int32, cast to int64 inside the graph.

ONNX Runtime's WebGPU plugin (0.5.0, as bundled by RailReaderCore) aborts the process on an int64
graph input ("OrtDevice ... Invalid memory type: -1"). Kokoro's only int64 input is 'tokens'. Taking
it as int32 and casting it straight back leaves every weight and every other node unchanged, so the
model computes the same function; token ids are far below 2**31. See ADR-0007.

Usage: python kokoro_int32_tokens.py <kokoro.onnx> <out.onnx>
"""
import sys

import onnx
from onnx import TensorProto, helper


def main(src: str, dst: str) -> None:
    model = onnx.load(src)
    graph = model.graph
    tokens = next((i for i in graph.input if i.name == "tokens"), None)
    if tokens is None:
        sys.exit(f"{src} has no 'tokens' input; it is not a KokoroSharp-compatible Kokoro export.")
    elem = tokens.type.tensor_type.elem_type
    if elem == TensorProto.INT32:
        sys.exit(f"{src} already takes int32 tokens.")
    if elem != TensorProto.INT64:
        sys.exit(f"{src}: unexpected 'tokens' type {TensorProto.DataType.Name(elem)}.")

    tokens.type.tensor_type.elem_type = TensorProto.INT32
    for node in graph.node:
        node.input[:] = ["tokens_int64" if name == "tokens" else name for name in node.input]
    graph.node.insert(0, helper.make_node("Cast", ["tokens"], ["tokens_int64"], to=TensorProto.INT64, name="tokens_to_int64"))
    onnx.checker.check_model(model)
    onnx.save(model, dst)


if __name__ == "__main__":
    if len(sys.argv) != 3:
        sys.exit(__doc__)
    main(sys.argv[1], sys.argv[2])

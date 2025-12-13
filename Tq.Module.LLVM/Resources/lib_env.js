const memory = new WebAssembly.Memory({initial: 1});
const memoryView = new DataView(memory.buffer);
const table = new WebAssembly.Table({ initial: 16, element: 'anyfunc' });
let stackPointer = 0x0FFFF;

export const env = {
    "__linear_memory": memory,
    "__stack_pointer": new WebAssembly.Global({ value: "i32", mutable: true }, stackPointer),
    "__indirect_function_table": table,
    "__multi3": i128_multiply,

    "__aligned_alloc": alligned_alloc,
    
    //"mem_grow": (d) => memory.grow(d),
    //"mem_size": () => memory.buffer.byteLength / 65536,
};
export const env_settings = {
    memory: memory,
    memoryView: memory,
    table: table,
};

function i128_multiply(aLow, aHigh, bLow, bHigh) {
    const a = (BigInt(aHigh) << 64n) | BigInt(aLow);
    const b = (BigInt(bHigh) << 64n) | BigInt(bLow);
    const res = a * b;
    console.log(a, "+", b, "=", res);
    return [Number(res & 0xFFFFFFFFFFFFFFFFn), Number(res >> 64n)];
}

function alligned_alloc(length, align) {
    console.log(`requested aligned allocation of ${length} bytes, align=${align}`);
    return 100;
}

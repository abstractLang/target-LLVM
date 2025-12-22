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
    
    "__write": write,
};
export const env_settings = {
    stdin: undefined,
    stdout: undefined,
    stderr: undefined,
};

let allocator = null;

function i128_multiply(aLow, aHigh, bLow, bHigh) {
    const a = (BigInt(aHigh) << 64n) | BigInt(aLow);
    const b = (BigInt(bHigh) << 64n) | BigInt(bLow);
    const res = a * b;
    return [Number(res & 0xFFFFFFFFFFFFFFFFn), Number(res >> 64n)];
}

function alligned_alloc(length, align) {
    console.log(`requested aligned allocation of ${length} bytes, align=${align}`);
    
    if (length === 0) return 0;
        
    return 100;
}

function write(fd, ptr, len) {
    console.log(`Trying to write to ${fd}: ${ptr}[..len]`);
    env_settings.stdout(readString(ptr, len))
}


function readString(ptr, len) {
    let bytes = new Uint8Array(memory.buffer, ptr, len);
    return new TextDecoder("utf-8").decode(bytes);
}

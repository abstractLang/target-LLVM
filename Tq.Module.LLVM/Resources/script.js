import { Std, std_settings } from './std.js';

const stdout = document.querySelector("#stdout");
const stdin = document.querySelector("#stdin");

const webassemblyMainPath = './main.wasm';

await _start();
async function _start()
{
    const memory = new WebAssembly.Memory({initial: 1});
    const memoryView = new DataView(memory.buffer);
    const table = new WebAssembly.Table({ initial: 16, element: 'anyfunc' });
    let stackPointer = 0x0FFFF;

    const wasmcode = fetch(webassemblyMainPath);
    const rootlibs = {
        env: {
            "__linear_memory": memory,
            "__stack_pointer": new WebAssembly.Global({ value: "i32", mutable: true }, stackPointer),
            "__indirect_function_table": table,
            "__multi3": i128_multiply,
            
            "mem_grow": (d) => memory.grow(d),
            "mem_size": () => memory.buffer.byteLength / 65536,
        },
        Std: Std
    };
    
    const wasminstance = (await WebAssembly.instantiateStreaming(wasmcode, rootlibs)).instance;
    const entrypoint = wasminstance.exports["main"];

    std_settings.stdout = append_simple_stdout;
    std_settings.memory = memoryView;
    
    try {
        append_stdout("control", "Program started\n");
        entrypoint();
        append_stdout("control", "Program finished\n");
    }
    catch (error) {
        append_stdout("error", "An unexpected error occurred: " + error + "\n");
    }
}

function append_simple_stdout(text) { append_stdout("", text); }

function append_stdout(classes, text)
{
    let oldtext = text;
    text = handle_escape(text);
    let clist = classes.split(" ").filter(e => e !== "");

    const newline = document.createElement("span");
    if (clist.length > 0) newline.classList.add(clist);
    newline.innerHTML = text;

    stdout.appendChild(newline);
}
function allow_stdin(mode)
{

    if (mode === "character") append_stdout("control", "todo allow stdin");
    else if (mode === "line") append_stdout("control", "todo allow stdin");

}

function handle_escape(text)
{
    // common escape characters
    text = text.replace(/\n/g, "<br>");
    text = text.replace(/\t/g, "&nbsp;&nbsp;&nbsp;&nbsp;");

    // placeholder CSI shit
    var csicolorpattern = /{Console\.CSIFGColor\.([a-zA-Z_][a-zA-Z0-9_]*)}/g;

    text = text.replace(csicolorpattern, (_, identifier) => `<span class="fg-${identifier}">`);
    text = text.replace("{Console.CSIGeneral.reset}", '</span>');

    return text;
}

function i128_multiply(aLow, aHigh, bLow, bHigh) {
    const a = (BigInt(aHigh) << 64n) | BigInt(aLow);
    const b = (BigInt(bHigh) << 64n) | BigInt(bLow);
    const res = a * b;
    console.log(a, "+", b, "=", res);
    return [Number(res & 0xFFFFFFFFFFFFFFFFn), Number(res >> 64n)];
}

import { env, env_settings } from './lib_env.js';

const stdout = document.querySelector("#stdout");
const stdin = document.querySelector("#stdin");

const webassemblyMainPath = './main.wasm';

async function _start()
{
    const wasmcode = fetch(webassemblyMainPath);
    const rootlibs = { env: env };
    
    const wasminstance = (await WebAssembly.instantiateStreaming(wasmcode, rootlibs)).instance;
    const entrypoint = wasminstance.exports["_start"];

    env_settings.stdin = read_stdin;
    env_settings.stdout = write_stdout;
    env_settings.stderr = write_stderr;
    
    try {
        append_console("control", "Program started\n");
        entrypoint();
        append_console("control", "Program finished\n");
    }
    catch (error) { append_console("error", "An unexpected error occurred: " + error + "\n"); }
}

const write_stdout = (stdout) => append_console("stdout", stdout); 
const write_stderr = (stdout) => append_console("stderr", stdout); 
function read_stdin() {
    alert("read stdin TODO");
}


function append_console(classes, text)
{
    let oldtext = text;
    text = handle_escape(text);
    let clist = classes.split(" ").filter(e => e !== "");

    const newline = document.createElement("span");
    if (clist.length > 0) newline.classList.add(clist);
    newline.innerHTML = text;

    stdout.appendChild(newline);
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


await _start();
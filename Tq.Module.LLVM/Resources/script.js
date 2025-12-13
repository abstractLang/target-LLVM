import { std, std_settings } from './lib_std.js';
import { env, env_settings } from './lib_env.js';

const stdout = document.querySelector("#stdout");
const stdin = document.querySelector("#stdin");

const webassemblyMainPath = './main.wasm';

await _start();
async function _start()
{
    const wasmcode = fetch(webassemblyMainPath);
    const rootlibs = {
        env: env,
        Std: std,
    };
    
    const wasminstance = (await WebAssembly.instantiateStreaming(wasmcode, rootlibs)).instance;
    const entrypoint = wasminstance.exports["_start"];

    std_settings.stdout = append_simple_stdout;
    std_settings.memory = env_settings.memory;
    
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

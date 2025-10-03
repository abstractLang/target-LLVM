/*
    ABSTRACT STD LIBRARY IMPLEMENTATION
    for webassembly target
*/

function stringPtrToString(str_ptr, str_len) {
    const bytes = new Uint8Array(
        std_settings.memory.buffer,
        std_settings.memory.byteOffset + str_ptr,
        str_len);
    return new TextDecoder("utf-8").decode(bytes);
}

function Console_write_i64(num) { std_settings.stdout(num.toString()); }
function Console_write_u64(num) { std_settings.stdout(BigInt.asUintN(64, num).toString()); }
function Console_write_string$Utf8(str_ptr, str_len) {
    std_settings.stdout("Std.Console.write(string(Utf8): " + stringPtrToString(str_ptr, str_len)); }

function Console_writeln_i64(num) { std_settings.stdout(num + "\n"); }
function Console_writeln_u64(num) { std_settings.stdout(BigInt.asUintN(64, num).toString() + "\n"); }
function Console_writeln_string$Utf8(str_ptr, str_len) {
    std_settings.stdout(stringPtrToString(str_ptr, str_len) + "\n"); }


export var std_settings = {
    stdout: (e) => console.error("Not implemented! ", e),
    memory: undefined,
}
export const Std = {
    "Console.write_i64": Console_write_i64, "Console.write_u64": Console_write_u64,
    "Console.write_string(Utf8)": Console_write_string$Utf8,
    
    "Console.writeln_i64": Console_writeln_i64, "Console.writeln_u64": Console_writeln_u64,
    "Console.writeln_string(Utf8)": Console_writeln_string$Utf8,
}

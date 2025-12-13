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

function console_write_signedInt(num) { std_settings.stdout(num.toString()); }
function console_write_unsignedInt(num) { std_settings.stdout(BigInt.asUintN(64, num).toString()); }
function console_write_string$Utf8(str_ptr, str_len) {
    std_settings.stdout(stringPtrToString(str_ptr, str_len)); }

function console_writeln_signedInt(num) { std_settings.stdout(num + "\n"); }
function console_writeln_unsignedInt(num) { std_settings.stdout(BigInt.asUintN(64, num).toString() + "\n"); }
function console_writeln_string$Utf8(str_ptr, str_len) {
    std_settings.stdout(stringPtrToString(str_ptr, str_len) + "\n"); }


export var std_settings = {
    stdout: (e) => console.error("Not implemented! ", e),
    memory: undefined,
}
export const std = {
    "Console.write(BigInt)": console_write_signedInt, "Console.write(UBigInt)": console_write_unsignedInt,
    "Console.write(string(Utf8))": console_write_string$Utf8,
    
    "Console.writeln(BigInt)": console_writeln_signedInt, "Console.writeln(UBigInt)": console_writeln_unsignedInt,
    "Console.writeln(string(Utf8))": console_writeln_string$Utf8,
    
    
    "Process.exit(iptr)": (status) => { throw { error: "exited", statusCode: status } },
}

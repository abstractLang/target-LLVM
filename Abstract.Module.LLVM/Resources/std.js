/*
    ABSTRACT STD LIBRARY IMPLEMENTATION
    for webassembly target
*/

export const StdSettings = {
    define_stdout: (e) => stdout = e,
    setup: setup,
}

function setup() {
    
}

let stdout = (e) => console.error("Not implemented! ", e);


function Console_write_i32(num) { stdout("Std.Console.write(i32): " + num); }
function Console_write_u32(num) { stdout("Std.Console.write(u32): " + (num >>> 0)); }
function Console_write_i64(num) { stdout("Std.Console.write(i64): " + num); }
function Console_write_u64(num) { stdout("Std.Console.write(u64): " + BigInt.asUintN(64, num).toString()); }

function Console_writeln_i32(num) { stdout("Std.Console.writeln(i32): " + num + "\n"); }
function Console_writeln_u32(num) { stdout("Std.Console.writeln(u32): " + (num >>> 0) + "\n"); }
function Console_writeln_i64(num) { stdout("Std.Console.writeln(i64): " + num + "\n"); }
function Console_writeln_u64(num) { stdout("Std.Console.writeln(u64): " + BigInt.asUintN(64, num).toString() + "\n"); }


export const Std = {
    "Console.write_i32": Console_write_i32, "Console.write_u32": Console_write_u32,
    "Console.write_i64": Console_write_i64, "Console.write_u64": Console_write_u64,
    
    "Console.writeln_i64": Console_writeln_i64, "Console.writeln_u64": Console_writeln_u64,
    "Console.writeln_i32": Console_writeln_i32, "Console.writeln_u32": Console_writeln_u32,

}

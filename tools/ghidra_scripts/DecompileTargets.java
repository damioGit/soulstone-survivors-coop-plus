// Decompiles a fixed list of functions (given as RVA|Label lines in an input file) and writes
// the resulting pseudo-C for each to an output file. RVAs are relative to the image base, which
// is how Il2CppDumper reports them (RVA = VA - imageBase for this PE).
//
// Usage (headless): -postScript DecompileTargets.java <rvaListFile> <outputFile>
import ghidra.app.script.GhidraScript;
import ghidra.app.decompiler.DecompInterface;
import ghidra.app.decompiler.DecompileOptions;
import ghidra.app.decompiler.DecompileResults;
import ghidra.program.model.address.Address;
import ghidra.program.model.listing.Function;

import java.io.BufferedReader;
import java.io.FileReader;
import java.io.FileWriter;
import java.io.PrintWriter;

public class DecompileTargets extends GhidraScript {

    @Override
    public void run() throws Exception {
        String[] args = getScriptArgs();
        if (args.length < 2) {
            println("Usage: DecompileTargets.java <rvaListFile> <outputFile>");
            return;
        }
        String rvaListFile = args[0];
        String outputFile = args[1];

        Address imageBase = currentProgram.getImageBase();

        DecompInterface decomp = new DecompInterface();
        decomp.setOptions(new DecompileOptions());
        decomp.openProgram(currentProgram);

        try (BufferedReader reader = new BufferedReader(new FileReader(rvaListFile));
             PrintWriter writer = new PrintWriter(new FileWriter(outputFile))) {

            String line;
            while ((line = reader.readLine()) != null) {
                line = line.trim();
                if (line.isEmpty() || line.startsWith("#")) continue;

                String[] parts = line.split("\\|", 2);
                String rvaStr = parts[0].trim();
                String label = parts.length > 1 ? parts[1].trim() : rvaStr;

                long rva = Long.decode(rvaStr);
                Address addr = imageBase.add(rva);

                writer.println("=== " + label + " (RVA " + rvaStr + ", addr " + addr + ") ===");

                Function func = getFunctionAt(addr);
                if (func == null) {
                    func = createFunction(addr, label.replace('.', '_').replace(' ', '_'));
                }

                if (func == null) {
                    writer.println("<<could not create/find function at this address>>\n");
                    continue;
                }

                DecompileResults results = decomp.decompileFunction(func, 120, getMonitor());
                if (results != null && results.decompileCompleted()) {
                    writer.println(results.getDecompiledFunction().getC());
                } else {
                    String err = results != null ? results.getErrorMessage() : "unknown error";
                    writer.println("<<decompilation failed: " + err + ">>");
                }
                writer.println();
                writer.flush();
                println("Decompiled: " + label);
            }
        }

        decomp.dispose();
        println("DONE. Output written to " + outputFile);
    }
}

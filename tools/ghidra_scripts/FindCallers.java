// Finds every function that references (calls) a given address, and decompiles each of those
// CALLERS (not the target itself) - so we can see the orchestrator that drives several
// UpdateAll()-style per-frame systems, instead of discovering them one at a time.
//
// Usage (headless): -postScript FindCallers.java <targetRvaHex> <outputFile>
import ghidra.app.script.GhidraScript;
import ghidra.app.decompiler.DecompInterface;
import ghidra.app.decompiler.DecompileOptions;
import ghidra.app.decompiler.DecompileResults;
import ghidra.program.model.address.Address;
import ghidra.program.model.listing.Function;
import ghidra.program.util.ProgramLocation;
import ghidra.program.model.symbol.Reference;
import ghidra.program.model.symbol.ReferenceIterator;

import java.io.FileWriter;
import java.io.PrintWriter;
import java.util.HashSet;
import java.util.Set;

public class FindCallers extends GhidraScript {

    @Override
    public void run() throws Exception {
        String[] args = getScriptArgs();
        if (args.length < 2) {
            println("Usage: FindCallers.java <targetRvaHex> <outputFile>");
            return;
        }
        long rva = Long.decode(args[0].trim());
        String outputFile = args[1];

        Address imageBase = currentProgram.getImageBase();
        Address target = imageBase.add(rva);

        DecompInterface decomp = new DecompInterface();
        decomp.setOptions(new DecompileOptions());
        decomp.openProgram(currentProgram);

        try (PrintWriter writer = new PrintWriter(new FileWriter(outputFile))) {
            writer.println("=== Callers of RVA " + args[0] + " (addr " + target + ") ===");

            ReferenceIterator refs = currentProgram.getReferenceManager().getReferencesTo(target);
            Set<Address> callerEntryPoints = new HashSet<>();

            while (refs.hasNext()) {
                Reference ref = refs.next();
                Address fromAddr = ref.getFromAddress();
                Function callerFunc = getFunctionContaining(fromAddr);
                if (callerFunc != null) {
                    callerEntryPoints.add(callerFunc.getEntryPoint());
                }
            }

            writer.println("Found " + callerEntryPoints.size() + " distinct calling function(s).");
            writer.println();

            for (Address entry : callerEntryPoints) {
                Function func = getFunctionAt(entry);
                long callerRva = entry.subtract(imageBase);
                writer.println("--- Caller at RVA 0x" + Long.toHexString(callerRva) + " (addr " + entry + ") ---");

                DecompileResults results = decomp.decompileFunction(func, 120, getMonitor());
                if (results != null && results.decompileCompleted()) {
                    writer.println(results.getDecompiledFunction().getC());
                } else {
                    String err = results != null ? results.getErrorMessage() : "unknown error";
                    writer.println("<<decompilation failed: " + err + ">>");
                }
                writer.println();
                writer.flush();
                println("Decompiled caller at RVA 0x" + Long.toHexString(callerRva));
            }
        }

        decomp.dispose();
        println("DONE. Output written to " + outputFile);
    }
}

# Baked Lexicon Generation (Full Dictionary)

This folder contains the static C# files generated for the full 450,000+ word dictionary. They have been "baked" to prevent the 3-minute build time required for generation.

## How to Regenerate

If you update the source word list (`Resources/words.txt`) and need to update these files:

1.  **Clean Up**: Delete all `.cs` files in this `Generated` folder.

2.  **Enable Generator**:
    Open `ABC.DiscoveryCity.Words.csproj` and uncomment/add the Source Generator reference and configuration:

    ```xml
    <PropertyGroup>
        <!-- ... other properties ... -->
        <EmitCompilerGeneratedFiles>true</EmitCompilerGeneratedFiles>
        <CompilerGeneratedFilesOutputPath>$(BaseIntermediateOutputPath)Generated</CompilerGeneratedFilesOutputPath>
    </PropertyGroup>

    <ItemGroup>
        <!-- Change None to AdditionalFiles -->
        <AdditionalFiles Include="Resources\words.txt" />
    </ItemGroup>

    <ItemGroup>
        <ProjectReference Include="..\ABC.DiscoveryCity.Generator\ABC.DiscoveryCity.Generator.csproj" OutputItemType="Analyzer" ReferenceOutputAssembly="false" />
    </ItemGroup>
    ```

3.  **Build**: Run `dotnet build`. **Warning: This will take ~3 minutes.**

4.  **Copy**: Copy the new files from the `obj` folder to this folder.
    *   Source: `obj\Debug\net10.0\Generated\ABC.DiscoveryCity.Generator\ABC.DiscoveryCity.Words.Generator.WordsGenerator\*.cs`
    *   Destination: `Generated\`

5.  **Disable Generator**: Revert the changes to `.csproj` to "freeze" the code again.

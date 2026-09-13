#include "AssetActions/CSAssetTypeAction_CSBlueprint.h"
#include "Types/CSBlueprint.h"

#include "CSPathsUtilities.h"
#include "UnrealSharpEditor.h"
#include "Utilities/CSEditorUtilities.h"

#include "HAL/FileManager.h"
#include "HAL/PlatformMisc.h"
#include "HAL/PlatformProcess.h"
#include "Misc/Paths.h"
#include "Styling/AppStyle.h"

namespace FCSAssetTypeAction_CSBlueprintPrivate
{
	/**
	 * The blueprint generated for a C# type is named after the type without its single letter prefix
	 * (UTkUISubsystem -> "TkUISubsystem"), so the asset name already matches the source file name. Stripping the
	 * prefix is only a safety net for names that still carry it.
	 */
	FString StripTypePrefix(const FString& TypeName)
	{
		if (TypeName.Len() > 1 && FChar::IsUpper(TypeName[1]))
		{
			const TCHAR FirstCharacter = TypeName[0];
			const bool bHasTypePrefix = FirstCharacter == TEXT('U') || FirstCharacter == TEXT('A')
				|| FirstCharacter == TEXT('I') || FirstCharacter == TEXT('F') || FirstCharacter == TEXT('E');

			if (bHasTypePrefix)
			{
				return TypeName.RightChop(1);
			}
		}

		return TypeName;
	}

	bool FindSourceFile(const FString& TypeName, FString& OutSourceFilePath)
	{
		// The asset name is the C# short name, which is what the file is named after.
		TArray<FString> CandidateFileNames;
		CandidateFileNames.Add(TypeName);
		CandidateFileNames.Add(StripTypePrefix(TypeName));

		// With namespace support enabled blueprint names look like "Namespace_TypeName"; the type name is the last segment.
		int32 LastSeparatorIndex = INDEX_NONE;
		if (TypeName.FindLastChar(TEXT('_'), LastSeparatorIndex))
		{
			const FString ShortTypeName = TypeName.RightChop(LastSeparatorIndex + 1);
			CandidateFileNames.Add(ShortTypeName);
			CandidateFileNames.Add(StripTypePrefix(ShortTypeName));
		}

		TArray<FString> FoundFiles;
		for (const FString& CandidateFileName : CandidateFileNames)
		{
			IFileManager::Get().FindFilesRecursive(FoundFiles, *UnrealSharp::Paths::GetScriptFolderDirectory(), *(CandidateFileName + TEXT(".cs")), true, false);

			if (!FoundFiles.IsEmpty())
			{
				OutSourceFilePath = FoundFiles[0];
				return true;
			}
		}

		return false;
	}

#if PLATFORM_WINDOWS
	FString FindFirstExecutable(const TArray<FString>& CandidatePaths)
	{
		for (const FString& CandidatePath : CandidatePaths)
		{
			if (FPaths::FileExists(CandidatePath))
			{
				return CandidatePath;
			}
		}

		return FString();
	}

	/**
	 * Windows has no association for .cs on most machines, so opening through the shell alone would end up in the
	 * "How do you want to open this file?" dialog. Look for the IDEs in their default install locations as a fallback.
	 */
	FString FindInstalledIdeExecutable()
	{
		const FString LocalAppData = FPlatformMisc::GetEnvironmentVariable(TEXT("LOCALAPPDATA"));
		const FString ProgramFiles = FPlatformMisc::GetEnvironmentVariable(TEXT("ProgramFiles"));

		TArray<FString> Candidates;
		Candidates.Add(LocalAppData / TEXT("Programs/Rider/bin/rider64.exe"));
		Candidates.Add(ProgramFiles / TEXT("JetBrains/Rider/bin/rider64.exe"));
		Candidates.Add(LocalAppData / TEXT("Programs/Microsoft VS Code/Code.exe"));
		Candidates.Add(ProgramFiles / TEXT("Microsoft VS Code/Code.exe"));

		const FString Executable = FindFirstExecutable(Candidates);
		if (!Executable.IsEmpty())
		{
			return Executable;
		}

		// Visual Studio installs into <Product><Year>/<Edition>, so glob for devenv.exe instead of guessing.
		TArray<FString> VisualStudioExecutables;
		const FString VisualStudioDirectory = ProgramFiles / TEXT("Microsoft Visual Studio");
		if (FPaths::DirectoryExists(VisualStudioDirectory))
		{
			IFileManager::Get().FindFilesRecursive(VisualStudioExecutables, *VisualStudioDirectory, TEXT("devenv.exe"), true, false);
		}

		return VisualStudioExecutables.IsEmpty() ? FString() : VisualStudioExecutables[0];
	}

	bool LaunchInIde(const FString& IdeExecutable, const FString& SourceFilePath)
	{
		const FString QuotedSourceFilePath = FString::Printf(TEXT("\"%s\""), *SourceFilePath);
		FString Arguments = QuotedSourceFilePath;

		if (IdeExecutable.Contains(TEXT("rider64.exe")))
		{
			// rider64.exe [/project/dir] [--line <n>] <file>: without the project directory the file lands in a
			// temporary single-file project instead of the managed C# solution.
			const FString ProjectDirectory = FPaths::GetPath(UnrealSharp::Paths::GetPathToManagedSolution());

			if (FPaths::DirectoryExists(ProjectDirectory))
			{
				Arguments = FString::Printf(TEXT("\"%s\" %s"), *ProjectDirectory, *QuotedSourceFilePath);
			}
		}
		else if (IdeExecutable.Contains(TEXT("Code.exe")))
		{
			Arguments = FString::Printf(TEXT("--reuse-window %s"), *QuotedSourceFilePath);
		}
		else
		{
			// Visual Studio: /Edit adds the file to a running instance when there is one.
			Arguments = FString::Printf(TEXT("/Edit %s"), *QuotedSourceFilePath);
		}

		return FPlatformProcess::CreateProc(*IdeExecutable, *Arguments, true, false, false, nullptr, 0, nullptr, nullptr).IsValid();
	}
#endif

	bool OpenSourceFile(const FString& SourceFilePath)
	{
#if PLATFORM_WINDOWS
		// Prefer an installed IDE: it is the only path that opens the file inside the managed C# solution, and it
		// avoids the "How do you want to open this file?" dialog Windows shows for .cs files without an association.
		const FString IdeExecutable = FindInstalledIdeExecutable();
		if (!IdeExecutable.IsEmpty() && LaunchInIde(IdeExecutable, SourceFilePath))
		{
			return true;
		}
#endif

		// Fall back to the shell, which uses the user's default application or asks which one to use.
		return FPlatformProcess::LaunchFileInDefaultExternalApplication(*SourceFilePath, nullptr, ELaunchVerb::Open, true);
	}
}

UClass* FCSAssetTypeAction_CSBlueprint::GetSupportedClass() const
{
	return UCSBlueprint::StaticClass();
}

void FCSAssetTypeAction_CSBlueprint::OpenAssetEditor(const TArray<UObject*>& InObjects, TSharedPtr<class IToolkitHost> EditWithinLevelEditor)
{
	for (UObject* Object : InObjects)
	{
		UCSBlueprint* Blueprint = Cast<UCSBlueprint>(Object);

		if (!Blueprint)
		{
			continue;
		}

		FString SourceFilePath;
		if (!FCSAssetTypeAction_CSBlueprintPrivate::FindSourceFile(Blueprint->GetName(), SourceFilePath))
		{
			UE_LOG(LogUnrealSharpEditor, Warning, TEXT("No C# source file found for class '%s' in %s."),
				*Blueprint->GetName(), *UnrealSharp::Paths::GetScriptFolderDirectory());

			FCSEditorUtilities::MakeNotification(FSlateIcon(FAppStyle::GetAppStyleSetName(), TEXT("Icons.Warning")),
				FString::Printf(TEXT("No C# source file found for class '%s'."), *Blueprint->GetName()));
			continue;
		}

		if (FCSAssetTypeAction_CSBlueprintPrivate::OpenSourceFile(SourceFilePath))
		{
			UE_LOG(LogUnrealSharpEditor, Display, TEXT("Opening C# source file '%s' for class '%s'."),
				*SourceFilePath, *Blueprint->GetName());
			continue;
		}

		UE_LOG(LogUnrealSharpEditor, Warning, TEXT("Failed to open C# source file '%s'."), *SourceFilePath);
		FCSEditorUtilities::MakeNotification(FSlateIcon(FAppStyle::GetAppStyleSetName(), TEXT("Icons.Warning")),
			FString::Printf(TEXT("Failed to open C# source file '%s'."), *SourceFilePath));
	}
}

bool FCSAssetTypeAction_CSBlueprint::SupportsOpenedMethod(const EAssetTypeActivationOpenedMethod OpenedMethod) const
{
	return OpenedMethod == EAssetTypeActivationOpenedMethod::Edit;
}

FText FCSAssetTypeAction_CSBlueprint::GetName() const
{
	return FText::FromString(FString::Printf(TEXT("C# Class")));
}

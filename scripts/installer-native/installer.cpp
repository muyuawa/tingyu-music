#include <windows.h>
#include <msi.h>
#include <msiquery.h>
#include <shlobj.h>
#include <shobjidl.h>
#include <filesystem>
#include <string>
#include <iostream>
#include <stdexcept>
namespace fs=std::filesystem;

// Delete only an explicitly owned child directory. Junctions and symlinks are
// unlinked themselves; their destinations are never enumerated or deleted.
static void RemoveTree(const fs::path& path) {
    const DWORD attributes=GetFileAttributesW(path.c_str());
    if(attributes==INVALID_FILE_ATTRIBUTES) {
        if(GetLastError()==ERROR_FILE_NOT_FOUND || GetLastError()==ERROR_PATH_NOT_FOUND)return;
        throw std::runtime_error("Cannot read data directory");
    }
    if((attributes&FILE_ATTRIBUTE_DIRECTORY) && !(attributes&FILE_ATTRIBUTE_REPARSE_POINT))
        for(const auto& entry:fs::directory_iterator(path))RemoveTree(entry.path());
    if(attributes&FILE_ATTRIBUTE_READONLY)
        if(!SetFileAttributesW(path.c_str(),attributes&~FILE_ATTRIBUTE_READONLY))throw std::runtime_error("Cannot unlock data file");
    if(!(attributes&FILE_ATTRIBUTE_DIRECTORY ? RemoveDirectoryW(path.c_str()) : DeleteFileW(path.c_str())))
        throw std::runtime_error("Cannot remove application data");
}
static void RemoveOwnedChild(fs::path parent,const wchar_t* child) {
    parent=parent.lexically_normal();
    if(parent.filename().empty() && parent!=parent.root_path())parent=parent.parent_path();
    if(!parent.is_absolute() || parent==parent.root_path())throw std::runtime_error("Unsafe data parent");
    const auto target=(parent/child).lexically_normal();
    if(target.parent_path()!=parent.lexically_normal())throw std::runtime_error("Unsafe data target");
    for(auto ancestor=parent;!ancestor.empty();ancestor=ancestor.parent_path()) {
        const DWORD attributes=GetFileAttributesW(ancestor.c_str());
        if(attributes!=INVALID_FILE_ATTRIBUTES && (attributes&FILE_ATTRIBUTE_REPARSE_POINT))throw std::runtime_error("Linked data ancestor");
        if(ancestor==ancestor.root_path())break;
    }
    RemoveTree(target);
}
extern "C" __declspec(dllexport) UINT __stdcall CleanupTingyuData(MSIHANDLE session) {
    try {
        // Resolve the current user's known folder rather than trusting a supplied
        // recursive-delete path. This action is impersonated, never elevated.
        PWSTR local=nullptr;
        if(FAILED(SHGetKnownFolderPath(FOLDERID_LocalAppData,KF_FLAG_DONT_VERIFY,nullptr,&local)))return ERROR_INSTALL_FAILURE;
        const fs::path parent(local);CoTaskMemFree(local);
        RemoveOwnedChild(parent,L"MuyuMusic");
        wchar_t temporary[32768]{};
        if(GetTempPathW(32768,temporary)>0)RemoveOwnedChild(fs::path(temporary),L"MuyuMusic");
        // Previous portable installations can retain portable.marker on upgrade.
        wchar_t installed[32768]{};DWORD length=32768;
        if(MsiGetPropertyW(session,L"CustomActionData",installed,&length)==ERROR_SUCCESS && installed[0]) {
            const fs::path install=fs::path(installed).lexically_normal();
            if(fs::is_regular_file(install/L"portable.marker")) {
                RemoveOwnedChild(install,L"data");
                fs::remove(install/L"portable.marker");
            }
        }
        HKEY run=nullptr;
        if(RegOpenKeyExW(HKEY_CURRENT_USER,L"Software\\Microsoft\\Windows\\CurrentVersion\\Run",0,KEY_SET_VALUE,&run)==ERROR_SUCCESS) {
            RegDeleteValueW(run,L"MuyuMusic");RegCloseKey(run);
        }
        // Remove only our OpenWith value, preserving all other applications and
        // the user's default association for each extension.
        HKEY classes=nullptr;
        if(RegOpenKeyExW(HKEY_CURRENT_USER,L"Software\\Classes",0,KEY_READ|KEY_WRITE,&classes)==ERROR_SUCCESS) {
            wchar_t name[256]{};DWORD size=256;
            for(DWORD index=0;RegEnumKeyExW(classes,index,name,&size,nullptr,nullptr,nullptr,nullptr)==ERROR_SUCCESS;++index,size=256) {
                if(name[0]!=L'.')continue;
                HKEY openWith=nullptr;
                const std::wstring sub=std::wstring(name)+L"\\OpenWithProgids";
                if(RegOpenKeyExW(classes,sub.c_str(),0,KEY_SET_VALUE,&openWith)==ERROR_SUCCESS) {
                    RegDeleteValueW(openWith,L"MuyuMusic.Audio");RegCloseKey(openWith);
                }
            }
            RegDeleteTreeW(classes,L"MuyuMusic.Audio");RegCloseKey(classes);
        }
        return ERROR_SUCCESS;
    } catch(...) {
        MSIHANDLE record=MsiCreateRecord(1);
        MsiRecordSetStringW(record,0,L"Tingyu could not completely remove its application data (locked file or unsafe linked path).");
        MsiProcessMessage(session,INSTALLMESSAGE_ERROR,record);MsiCloseHandle(record);
        return ERROR_INSTALL_FAILURE;
    }
}

static fs::path ValidInstall(const fs::path& path) {
    std::error_code error;
    if(!path.is_absolute() || !fs::is_regular_file(path/L"Muyu.App.exe",error) ||
       !fs::is_regular_file(path/L"Muyu.App.dll",error) || !fs::is_regular_file(path/L"MuyuAudio.dll",error)) return {};
    return path.lexically_normal();
}
static std::wstring RegistryDirectory() {
    wchar_t value[32768]{}; DWORD bytes=sizeof(value);
    if(RegGetValueW(HKEY_CURRENT_USER,L"Software\\MuyuMusic",L"InstallDir",RRF_RT_REG_SZ,nullptr,value,&bytes)==ERROR_SUCCESS)return value;
    return {};
}
static fs::path ShortcutDirectory(const fs::path& link) {
    IShellLinkW* shell=nullptr;
    if(FAILED(CoCreateInstance(CLSID_ShellLink,nullptr,CLSCTX_INPROC_SERVER,IID_PPV_ARGS(&shell))))return {};
    IPersistFile* file=nullptr; fs::path result;
    if(SUCCEEDED(shell->QueryInterface(IID_PPV_ARGS(&file)))) {
        if(SUCCEEDED(file->Load(link.c_str(),STGM_READ))) {
            wchar_t target[32768]{};
            if(SUCCEEDED(shell->GetPath(target,32768,nullptr,SLGP_RAWPATH)) && fs::path(target).filename()==L"Muyu.App.exe")result=ValidInstall(fs::path(target).parent_path());
        }
        file->Release();
    }
    shell->Release();return result;
}
static fs::path FindInstall() {
    if(auto path=ValidInstall(RegistryDirectory());!path.empty())return path;
    wchar_t product[39]{};
    for(DWORD i=0;MsiEnumRelatedProductsW(L"{3F0165E1-33B1-41E2-9C92-29AB5143C3C2}",0,i,product)==ERROR_SUCCESS;++i) {
        wchar_t location[32768]{};DWORD length=32768;
        if(MsiGetProductInfoW(product,INSTALLPROPERTY_INSTALLLOCATION,location,&length)==ERROR_SUCCESS)
            if(auto path=ValidInstall(location);!path.empty())return path;
    }
    const HRESULT com=CoInitializeEx(nullptr,COINIT_APARTMENTTHREADED);
    PWSTR programs=nullptr;fs::path result;
    if(SUCCEEDED(SHGetKnownFolderPath(FOLDERID_Programs,KF_FLAG_DONT_VERIFY,nullptr,&programs))) {
        for(const auto name:{L"听屿.lnk",L"Muyu Music.lnk"}) {
            result=ShortcutDirectory(fs::path(programs)/name);if(!result.empty())break;
        }
        CoTaskMemFree(programs);
    }
    if(SUCCEEDED(com))CoUninitialize();
    if(!result.empty())return result;
    // Migration for the previously delivered file-copy installation.
    if(auto path=ValidInstall(L"D:\\MuyuMusic");!path.empty())return path;
    wchar_t local[32768]{};
    if(GetEnvironmentVariableW(L"LOCALAPPDATA",local,32768)>0) {
        for(const auto name:{L"Tingyu",L"Muyu Music"})
            if(auto path=ValidInstall(fs::path(local)/L"Programs"/name);!path.empty())return path;
    }
    return {};
}
extern "C" __declspec(dllexport) UINT __stdcall DetectTingyuInstall(MSIHANDLE session) {
    try {
        wchar_t requested[32768]{};DWORD requestedLength=32768;
        MsiGetPropertyW(session,L"MUYU_PREVIOUS_INSTALL",requested,&requestedLength);
        auto existing=ValidInstall(requested);
        if(existing.empty())existing=FindInstall();
        if(!existing.empty()) {
            MsiSetPropertyW(session,L"INSTALLROOT",existing.parent_path().c_str());
            MsiSetPropertyW(session,L"INSTALLFOLDER",existing.c_str());
            MsiSetPropertyW(session,L"MUYU_PREVIOUS_INSTALL",existing.c_str());
            MsiSetPropertyW(session,L"MUYU_EXISTING_INSTALL",L"1");
        }
    } catch(...) { /* Discovery is optional; a clean install remains available. */ }
    return ERROR_SUCCESS;
}
extern "C" __declspec(dllexport) UINT __stdcall RestoreTingyuInstallPath(MSIHANDLE session) {
    wchar_t previous[32768]{};DWORD length=32768;
    if(MsiGetPropertyW(session,L"MUYU_PREVIOUS_INSTALL",previous,&length)!=ERROR_SUCCESS || !previous[0])return ERROR_SUCCESS;
    const UINT result=MsiSetTargetPathW(session,L"INSTALLFOLDER",previous);
    if(result==ERROR_SUCCESS)MsiSetPropertyW(session,L"INSTALLFOLDER",previous);
    return result;
}
#ifdef INSTALLER_PROBE
int wmain(int argc,wchar_t** argv) {
    if(argc==3 && std::wstring(argv[1])==L"--test-cleanup") {
        try{RemoveOwnedChild(fs::path(argv[2]),L"owned");return 0;}catch(...){return 7;}
    }
    if(argc<2 || argc>4)return 2;
    MsiSetInternalUI(INSTALLUILEVEL_NONE,nullptr);
    MSIHANDLE session=0;
    if(MsiOpenPackageExW(argv[1],MSIOPENPACKAGEFLAGS_IGNOREMACHINESTATE,&session)!=ERROR_SUCCESS)return 3;
    if(argc==4 && std::wstring(argv[2])==L"--existing") {
        const auto existing=ValidInstall(argv[3]);if(existing.empty()){MsiCloseHandle(session);return 6;}
        MsiSetPropertyW(session,L"INSTALLROOT",existing.parent_path().c_str());
        MsiSetPropertyW(session,L"MUYU_PREVIOUS_INSTALL",existing.c_str());
        MsiSetPropertyW(session,L"MUYU_EXISTING_INSTALL",L"1");
    }
    else if(argc==3)MsiSetPropertyW(session,L"INSTALLROOT",argv[2]);
    else DetectTingyuInstall(session);
    for(const auto action:{L"CostInitialize",L"FileCost",L"CostFinalize"})
        if(MsiDoActionW(session,action)!=ERROR_SUCCESS){MsiCloseHandle(session);return 4;}
    if(RestoreTingyuInstallPath(session)!=ERROR_SUCCESS){MsiCloseHandle(session);return 5;}
    for(const auto name:{L"INSTALLROOT",L"INSTALLFOLDER",L"MUYU_EXISTING_INSTALL",L"MUYU_PREVIOUS_INSTALL"}) {
        wchar_t value[32768]{};DWORD length=32768;
        MsiGetPropertyW(session,name,value,&length);std::wcout<<name<<L"="<<value<<L"\n";
    }
    wchar_t target[32768]{};DWORD targetLength=32768;
    MsiGetTargetPathW(session,L"INSTALLFOLDER",target,&targetLength);std::wcout<<L"RESOLVED_TARGET="<<target<<L"\n";
    MsiCloseHandle(session);return 0;
}
#endif

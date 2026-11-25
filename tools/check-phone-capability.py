#!/usr/bin/env python3
"""Verifica che la capability riservata WP8.1 finisca nel pacchetto ARM."""
import sys
import zipfile
import xml.etree.ElementTree as ET

ROOT = "https://schemas.microsoft.com/phone/2013/windowsphonereservedappinfo"
CAPABILITY = "ID_CAP_INTEROPSERVICES"
SOURCE = "Tailscale/WindowsPhoneReservedAppInfo.xml"
PACKAGE_MANIFEST = "Tailscale/Package.appxmanifest"
PROJECT = "Tailscale/Tailscale.csproj"
APPX = "Tailscale/bin/ARM/Release/Tailscale_1.0.0.0_Bundle/Tailscale_1.0.0.0_ARM.appx"


def capability_ids(xml_bytes):
    root = ET.fromstring(xml_bytes)
    if root.tag != "{%s}WindowsPhoneReservedAppInfo" % ROOT:
        raise ValueError("root element or namespace is invalid")
    return [node.attrib.get("Id") for node in root.findall(
        "{%s}SoftwareCapabilities/{%s}SoftwareCapability" % (ROOT, ROOT))]


def main():
    try:
        with open(SOURCE, "rb") as source_file:
            source_ids = capability_ids(source_file.read())
        if source_ids.count(CAPABILITY) != 1:
            raise ValueError("source must request %s exactly once" % CAPABILITY)

        package_manifest = ET.parse(PACKAGE_MANIFEST).getroot()
        appx_ns = "{http://schemas.microsoft.com/appx/2010/manifest}"
        manifest_ids = [node.attrib.get("Name") for node in package_manifest.findall(
            ".//%sCapabilities/%sCapability" % (appx_ns, appx_ns))]
        if CAPABILITY in manifest_ids:
            raise ValueError("reserved capability must not be added to Package.appxmanifest")

        project = ET.parse(PROJECT).getroot()
        msbuild_ns = "{http://schemas.microsoft.com/developer/msbuild/2003}"
        included = any(item.attrib.get("Include") == "WindowsPhoneReservedAppInfo.xml"
                       for item in project.findall(".//%sContent" % msbuild_ns))
        if not included:
            raise ValueError("reserved capability metadata is not included as package content")

        with zipfile.ZipFile(APPX) as package:
            if "WindowsPhoneReservedAppInfo.xml" not in package.namelist():
                raise ValueError("reserved capability metadata is missing from the ARM appx")
            packaged_ids = capability_ids(package.read("WindowsPhoneReservedAppInfo.xml"))
            if packaged_ids.count(CAPABILITY) != 1:
                raise ValueError("ARM appx does not contain %s exactly once" % CAPABILITY)
            appx_manifest = ET.fromstring(package.read("AppxManifest.xml"))
            packaged_manifest_ids = [node.attrib.get("Name") for node in appx_manifest.findall(
                ".//%sCapabilities/%sCapability" % (appx_ns, appx_ns))]
            if CAPABILITY in packaged_manifest_ids:
                raise ValueError("invalid capability leaked into AppxManifest.xml")
    except (OSError, ET.ParseError, zipfile.BadZipFile, KeyError, ValueError) as error:
        print("WP8.1 capability check failed: %s" % error)
        return 1

    print("WP8.1 interop capability ok (reserved metadata packaged)")
    return 0


if __name__ == "__main__":
    sys.exit(main())

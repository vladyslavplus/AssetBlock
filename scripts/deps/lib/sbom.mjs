import path from "node:path";
import fs from "node:fs";

function toNpmPurl(name, version) {
  if (name.startsWith("@")) {
    const [scope, pkg] = name.split("/");
    return `pkg:npm/${encodeURIComponent(scope)}/${pkg}@${version}`;
  }
  return `pkg:npm/${name}@${version}`;
}

function licenseObject(license) {
  if (!license) {
    return undefined;
  }
  if (/^[A-Za-z0-9.\-+]+$/.test(license)) {
    return [{ license: { id: license } }];
  }
  return [{ expression: license }];
}

export function writeCycloneDxBom({ outputPath, name, version, packages }) {
  const components = packages
    .slice()
    .sort((a, b) => a.name.localeCompare(b.name) || a.version.localeCompare(b.version))
    .map((pkg) => {
      const purl = toNpmPurl(pkg.name, pkg.version);
      const component = {
        type: "library",
        "bom-ref": purl,
        name: pkg.name,
        version: pkg.version,
        purl,
      };
      const licenses = licenseObject(pkg.license);
      if (licenses) {
        component.licenses = licenses;
      }
      return component;
    });

  const bom = {
    bomFormat: "CycloneDX",
    specVersion: "1.5",
    version: 1,
    metadata: {
      tools: [
        {
          vendor: "AssetBlock",
          name: "scripts/deps",
          version: "1.0.0",
        },
      ],
      component: {
        type: "application",
        name,
        version,
      },
    },
    components,
  };

  fs.mkdirSync(path.dirname(outputPath), { recursive: true });
  fs.writeFileSync(outputPath, `${JSON.stringify(bom, null, 2)}\n`, "utf8");
}

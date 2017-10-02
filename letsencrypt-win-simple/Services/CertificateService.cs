﻿using ACMESharp;
using ACMESharp.HTTP;
using ACMESharp.JOSE;
using ACMESharp.PKI;
using ACMESharp.PKI.RSA;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using LetsEncrypt.ACME.Simple.Extensions;

namespace LetsEncrypt.ACME.Simple.Services
{
    class CertificateService
    {
        private LogService _log;
        private string _certificateStore = "WebHosting";
        private string _configPath;
        private string _certificatePath;
        private Options _options;
        private AcmeClient _client;
        private X509Store _store;

        public CertificateService(Options options, LogService log, AcmeClient client, string configPath)
        {
            _log = log;
            _options = options;
            _client = client;
            _configPath = configPath;
            ParseCertificateStore();
            InitCertificatePath();
        }

        private void InitCertificatePath()
        {
            _certificatePath = Properties.Settings.Default.CertificatePath;
            if (string.IsNullOrWhiteSpace(_certificatePath)) {
                _certificatePath = _configPath;
            } else { 
                try {
                    Directory.CreateDirectory(_certificatePath);
                } catch (Exception ex) {
                    _log.Warning("Error creating the certificate directory, {_certificatePath}. Defaulting to config path. Error: {@ex}", _certificatePath, ex);
                    _certificatePath = _configPath;
                }
            }
            _log.Debug("Certificate folder: {_certificatePath}", _certificatePath);
        }

        private void ParseCertificateStore()
        {
            try
            {
                _certificateStore = Properties.Settings.Default.CertificateStore;
                _log.Debug("Certificate store: {_certificateStore}", _certificateStore);
            }
            catch (Exception ex)
            {
                _log.Warning("Error reading CertificateStore from config, defaulting to {_certificateStore} Error: {@ex}", _certificateStore, ex);
            }
        }

        public X509Store DefaultStore
        {
            get {
                if (_store == null)
                {
                    _store = new X509Store(_certificateStore, StoreLocation.LocalMachine);
                }
                return _store;
            }
        }

        public void InstallCertificate(X509Certificate2 certificate, X509Store store = null)
        {
            X509Store imStore = null;
            store = store ?? DefaultStore;
            try
            {
                imStore = new X509Store(StoreName.CertificateAuthority, StoreLocation.LocalMachine);
                //rootStore = new X509Store(StoreName.AuthRoot, StoreLocation.LocalMachine);
                store.Open(OpenFlags.OpenExistingOnly | OpenFlags.ReadWrite);
                imStore.Open(OpenFlags.OpenExistingOnly | OpenFlags.ReadWrite);
                //rootStore.Open(OpenFlags.OpenExistingOnly | OpenFlags.ReadWrite);
            }
            catch (Exception ex)
            {
                _log.Error("Error encountered while opening certificate store. Error: {@ex}", ex);
                throw new Exception(ex.Message);
            }
            _log.Debug("Opened Certificate Store {Name}", store.Name);

            try
            {
                _log.Debug("Adding certificate {FriendlyName} to store", certificate.FriendlyName);
                X509Chain chain = new X509Chain();
                chain.Build(certificate);
                foreach (var chainElement in chain.ChainElements)
                {
                    var cert = chainElement.Certificate;
                    if (cert.HasPrivateKey)
                    {
                        store.Add(cert);
                    }
                    else
                    {
                        imStore.Add(cert);
                    }
                }
            }
            catch (Exception ex)
            {
                _log.Error("Error saving certificate {@ex}", ex);
            }
            _log.Debug("Closing certificate store");
            store.Close();
            imStore.Close();
            //rootStore.Close();
        }

        public void UninstallCertificate(string thumbprint, X509Store store = null)
        {
            store = store ?? DefaultStore;
            try
            {
                store.Open(OpenFlags.OpenExistingOnly | OpenFlags.ReadWrite);
            }
            catch (Exception ex)
            {
                _log.Error(ex, "Error encountered while opening certificate store");
                throw;
            }

            _log.Debug("Opened certificate store {Name}", store.Name);
            try
            {
                X509Certificate2Collection col = store.Certificates;
                foreach (var cert in col)
                {
                    if (string.Equals(cert.Thumbprint, thumbprint, StringComparison.InvariantCultureIgnoreCase))
                    {
                        _log.Information("Removing certificate {cert}", cert.FriendlyName);
                        store.Remove(cert);
                    }
                }
                _log.Debug("Closing certificate store");
            }
            catch (Exception ex)
            {
                _log.Error(ex, "Error removing certificate");
                throw;
            }
            store.Close();
        }

        public X509Certificate2 GetCertificate(Target binding, X509Store store = null)
        {
            store = store ?? DefaultStore;
            X509Certificate2 ret = null;
            try
            {
                store.Open(OpenFlags.OpenExistingOnly | OpenFlags.ReadOnly);
            }
            catch (Exception ex)
            {
                _log.Error(ex, "Error encountered while opening certificate store");
                throw;
            }
            try
            {
                X509Certificate2Collection col = store.Certificates;
                foreach (var cert in col)
                {
                    if ((cert.Issuer.Contains("LE Intermediate") || cert.Issuer.Contains("Let's Encrypt")) && // Only delete Let's Encrypt certificates
                        cert.FriendlyName.StartsWith(binding.Host)) // match by friendly name
                    {
                        ret = cert;
                    }
                }
            }
            catch (Exception ex)
            {
                _log.Error(ex, "Error finding certificate");
                throw;
            }
            store.Close();
            return ret;
        }

        public X509Certificate2 RequestCertificate(Target binding)
        {
            // What are we going to get?
            var identifiers = binding.GetHosts(false);
            var fileName = FileNamePart(binding);
            var friendlyName = FriendlyName(binding);

            using (var cp = CertificateProvider.GetProvider("BouncyCastle"))
            {
                // Generate the private key and CSR
                var rsaPkp = GetRsaKeyParameters();
                var rsaKeys = cp.GeneratePrivateKey(rsaPkp);
                var csr = GetCsr(cp, identifiers, rsaKeys);
                byte[] derRaw;
                using (var bs = new MemoryStream())
                {
                    cp.ExportCsr(csr, EncodingFormat.DER, bs);
                    derRaw = bs.ToArray();
                }
                var derB64U = JwsHelper.Base64UrlEncode(derRaw);

                // Save request parameters to disk
                var keyGenFile = Path.Combine(_certificatePath, $"{fileName}-gen-key.json");
                using (var fs = new FileStream(keyGenFile, FileMode.Create))
                    cp.SavePrivateKey(rsaKeys, fs);

                var keyPemFile = Path.Combine(_certificatePath, $"{fileName}-key.pem");
                using (var fs = new FileStream(keyPemFile, FileMode.Create))
                    cp.ExportPrivateKey(rsaKeys, EncodingFormat.PEM, fs);

                var csrGenFile = Path.Combine(_certificatePath, $"{fileName}-gen-csr.json");
                using (var fs = new FileStream(csrGenFile, FileMode.Create))
                    cp.SaveCsr(csr, fs);

                var csrPemFile = Path.Combine(_certificatePath, $"{fileName}-csr.pem");
                using (var fs = new FileStream(csrPemFile, FileMode.Create))
                    cp.ExportCsr(csr, EncodingFormat.PEM, fs);

                // Request the certificate from Let's Encrypt 
                _log.Information($"Requesting certificate {friendlyName}");
                var certificateRequest = _client.RequestCertificate(derB64U);
                if (certificateRequest.StatusCode != HttpStatusCode.Created)
                {
                    var ex = new Exception($"Request status {certificateRequest.StatusCode}");
                    _log.Error(ex, "Certificate request failed");
                    throw ex;
                }

                // Main certicate and issuer certificate
                Crt certificate;
                Crt issuerCertificate;

                // Certificate request was successful, save the certificate itself
                var crtDerFile = Path.Combine(_certificatePath, $"{fileName}-crt.der");
                _log.Information("Saving certificate to {crtDerFile}", crtDerFile);
                using (var file = File.Create(crtDerFile))
                    certificateRequest.SaveCertificate(file);

                // Save certificate in PEM format too
                var crtPemFile = Path.Combine(_certificatePath, $"{fileName}-crt.pem");
                using (FileStream source = new FileStream(crtDerFile, FileMode.Open),
                    target = new FileStream(crtPemFile, FileMode.Create))
                {
                    certificate = cp.ImportCertificate(EncodingFormat.DER, source);
                    cp.ExportCertificate(certificate, EncodingFormat.PEM, target);
                }

                // Get issuer certificate and save in DER and PEM formats
                issuerCertificate = GetIssuerCertificate(certificateRequest, cp);
                var issuerDerFile = Path.Combine(_certificatePath, $"ca-{fileName}-crt.der");
                using (var target = new FileStream(issuerDerFile, FileMode.Create))
                    cp.ExportCertificate(issuerCertificate, EncodingFormat.DER, target);

                var issuerPemFile = Path.Combine(_certificatePath, $"ca-{fileName}-crt.pem");
                using (var target = new FileStream(issuerPemFile, FileMode.Create))
                    cp.ExportCertificate(issuerCertificate, EncodingFormat.PEM, target);

                // Save chain in PEM format
                var chainPemFile = Path.Combine(_certificatePath, $"{fileName}-chain.pem");
                using (FileStream intermediate = new FileStream(issuerPemFile, FileMode.Open),
                    certificateStrean = new FileStream(crtPemFile, FileMode.Open),
                    chain = new FileStream(chainPemFile, FileMode.Create))
                {
                    certificateStrean.CopyTo(chain);
                    intermediate.CopyTo(chain);
                }

                // All raw data has been saved, now generate the PFX file
                var pfxFile = PfxFilePath(binding);
                var pfxPassword = Properties.Settings.Default.PFXPassword;
                using (FileStream target = new FileStream(pfxFile, FileMode.Create))
                {
                    try
                    {
                        cp.ExportArchive(rsaKeys,
                            new[] { certificate, issuerCertificate },
                            ArchiveFormat.PKCS12,
                            target,
                            pfxPassword);
                    }
                    catch (Exception ex)
                    {
                        _log.Error("Error exporting archive {@ex}", ex);
                    }
                }

                X509KeyStorageFlags flags = X509KeyStorageFlags.MachineKeySet | X509KeyStorageFlags.PersistKeySet;
                if (Properties.Settings.Default.PrivateKeyExportable)
                {
                    _log.Debug("Set private key exportable");
                    flags |= X509KeyStorageFlags.Exportable;
                }

                // See http://paulstovell.com/blog/x509certificate2
                var res = new X509Certificate2(pfxFile, pfxPassword, flags);
                res.FriendlyName = friendlyName;
                return res;
            }
        }

        private string FriendlyName(Target target)
        {
            return $"{target.Host} {DateTime.Now.ToString(Properties.Settings.Default.FileDateFormat)}";
        }

        private string FileNamePart(Target target)
        {
            var identifiers = target.GetHosts(false);
            return identifiers.First();
        }

        public string PfxFilePath(Target target)
        {
            return PfxFilePath(FileNamePart(target));
        }

        public string PfxFilePath(string target)
        {
            return Path.Combine(_certificatePath, $"{target}-all.pfx");
        }

        /// <summary>
        /// Get the certificate signing request
        /// </summary>
        /// <param name="cp"></param>
        /// <param name="target"></param>
        /// <param name="identifiers"></param>
        /// <param name="rsaPk"></param>
        /// <returns></returns>
        private Csr GetCsr(CertificateProvider cp, List<string> identifiers, PrivateKey rsaPk)
        {
            var csr = cp.GenerateCsr(new CsrParams
            {
                Details = new CsrDetails()
                {
                    CommonName = identifiers.FirstOrDefault(),
                    AlternativeNames = identifiers
                }
            }, rsaPk, Crt.MessageDigest.SHA256);
            return csr;
        }

        /// <summary>
        /// Parameters to generate the key for
        /// </summary>
        /// <returns></returns>
        private RsaPrivateKeyParams GetRsaKeyParameters()
        {
            var rsaPkp = new RsaPrivateKeyParams();
            try
            {
                if (Properties.Settings.Default.RSAKeyBits >= 1024)
                {
                    rsaPkp.NumBits = Properties.Settings.Default.RSAKeyBits;
                    _log.Debug("RSAKeyBits: {RSAKeyBits}", Properties.Settings.Default.RSAKeyBits);
                }
                else
                {
                    _log.Warning("RSA Key Bits less than 1024 is not secure. Letting ACMESharp default key bits. http://openssl.org/docs/manmaster/crypto/RSA_generate_key_ex.html");
                }
            }
            catch (Exception ex)
            {
                _log.Warning("Unable to set RSA Key Bits, Letting ACMESharp default key bits, Error: {@ex}", ex);
            }
            return rsaPkp;
        }

        /// <summary>
        /// Get the issuer certificate
        /// </summary>
        /// <param name="certificate"></param>
        /// <param name="cp"></param>
        /// <returns></returns>
        private Crt GetIssuerCertificate(CertificateRequest certificate, CertificateProvider cp)
        {
            var linksEnum = certificate.Links;
            if (linksEnum != null)
            {
                var links = new LinkCollection(linksEnum);
                var upLink = links.GetFirstOrDefault("up");
                if (upLink != null)
                {
                    using (var web = new WebClient())
                    using (var stream = web.OpenRead(new Uri(new Uri(_options.BaseUri), upLink.Uri)))
                    {
                        return cp.ImportCertificate(EncodingFormat.DER, stream);
                    }
                }
            }
            return null;
        }
    }
}
